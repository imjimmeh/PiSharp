using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using PiSharp.Runtime.Subagents;
using Xunit;

namespace PiSharp.Runtime.Tests.Subagents;

/// <summary>
/// C1/C2 core-change tests: per-agent tool/skill restriction + tool injection at child creation,
/// structured-result capture, and the spawn guardrails (depth cap, disabled, self-recursion, spawns
/// allowlist) enforced in SubagentSessionService.CreateAsync.
/// </summary>
public sealed class SubagentSessionServiceGuardrailTests : IAsyncLifetime
{
    private string _tempRoot = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pisharp-subagent-guardrail-" + Guid.NewGuid().ToString("N"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { }
        }
        return Task.CompletedTask;
    }

    private string TempDir() => Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    // --- C1: tool/skill restriction + injection at child creation ---

    [Fact]
    public async Task CreateAsyncRestrictsChildActiveToolsAndRegistersInjectedTools()
    {
        var (service, _) = await CreateServiceAsync();
        var injected = new TestYieldTool();

        var handle = await service.CreateAsync(new SubagentSessionOptions(
            Tools: [injected],
            ActiveToolNames: ["read", "grep"]), CancellationToken.None);

        Assert.Equal(["read", "grep"], handle.Harness.ActiveToolNames);
        Assert.Contains(SubagentSessionService.YieldToolName, handle.Harness.AllToolNames);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncAppliesSelectedSkillNamesToChild()
    {
        var (service, _) = await CreateServiceAsync();

        var handle = await service.CreateAsync(new SubagentSessionOptions(
            SelectedSkillNames: ["skill-a", "skill-b"]), CancellationToken.None);

        Assert.Equal(["skill-a", "skill-b"], handle.Harness.SelectedSkillNames);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncStoresOutputSchemaDepthAndAgentNameOnHandle()
    {
        var (service, _) = await CreateServiceAsync();
        using var schemaDoc = JsonDocument.Parse("""{"type":"object","required":["findings"]}""");

        var handle = await service.CreateAsync(new SubagentSessionOptions(
            AgentName: "reviewer",
            OutputSchema: schemaDoc.RootElement.Clone(),
            Depth: 2,
            // The child runs at Depth + 1 = 3, so the cap must admit depth 3 (rule 3:
            // `Depth + 1 > MaxRecursionDepth` is blocked; `3 > 3` is not).
            SpawnPolicy: new SubagentSpawnPolicy(MaxRecursionDepth: 3)), CancellationToken.None);

        Assert.Equal(3, handle.Depth);
        Assert.Equal("reviewer", handle.AgentName);
        Assert.NotNull(handle.OutputSchema);
        Assert.Equal("findings", handle.OutputSchema!.Value.GetProperty("required")[0].GetString());

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    // --- C2: structured-result capture ---

    [Fact]
    public async Task PromptAsyncCapturesStructuredResultFromTerminatingYieldCall()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(
                new AgentHarnessOptions<JsonlSessionMetadata>(
                    session,
                    new ModelDescriptor("test", "test", "test"),
                    YieldThenDoneStream,
                    FakeCompletion,
                    [])),
            initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(
            Tools: [new TestYieldTool()],
            AgentName: "reviewer"), CancellationToken.None);

        var result = await service.PromptAsync(handle.SessionId, "produce findings", CancellationToken.None);

        Assert.NotNull(result.StructuredResult);
        Assert.Equal(JsonValueKind.Object, result.StructuredResult!.Value.ValueKind);
        Assert.Equal("warning", result.StructuredResult!.Value.GetProperty("findings")[0].GetProperty("severity").GetString());
        Assert.NotNull(handle.StructuredResult);
        Assert.True(JsonElement.DeepEquals(result.StructuredResult!.Value, handle.StructuredResult!.Value));

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task PromptAsyncLeavesStructuredResultNullWhenNoYieldCall()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);

        var result = await service.PromptAsync(handle.SessionId, "hello", CancellationToken.None);

        Assert.Null(result.StructuredResult);
        Assert.Null(handle.StructuredResult);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task PromptAsyncCapturesStructuredResultWhenSchemaPresent()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(
                new AgentHarnessOptions<JsonlSessionMetadata>(
                    session,
                    new ModelDescriptor("test", "test", "test"),
                    YieldThenDoneStream,
                    FakeCompletion,
                    [])),
            initial);
        var service = new SubagentSessionService(runtime);
        using var schemaDoc = JsonDocument.Parse("""{"type":"object","required":["findings"]}""");
        var handle = await service.CreateAsync(new SubagentSessionOptions(
            Tools: [new TestYieldTool()],
            OutputSchema: schemaDoc.RootElement.Clone()), CancellationToken.None);

        var result = await service.PromptAsync(handle.SessionId, "produce findings", CancellationToken.None);

        Assert.NotNull(result.StructuredResult);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    // --- C2a: spawn guardrails ---

    [Fact]
    public async Task CreateAsyncBlocksDisabledAgent()
    {
        var (service, _) = await CreateServiceAsync();
        var options = new SubagentSessionOptions(
            AgentName: "scout",
            SpawnPolicy: new SubagentSpawnPolicy(DisabledAgents: new HashSet<string>(["scout"], StringComparer.Ordinal)));

        var ex = await Assert.ThrowsAsync<SubagentSpawnBlockedException>(() =>
            service.CreateAsync(options, CancellationToken.None));

        Assert.Equal("scout", ex.Agent);
        Assert.Equal("disabled", ex.Reason);
    }

    [Fact]
    public async Task CreateAsyncBlocksSelfRecursionUnlessExplicitlyDeclaredInSpawns()
    {
        var (service, _) = await CreateServiceAsync();

        var blocked = await Assert.ThrowsAsync<SubagentSpawnBlockedException>(() =>
            service.CreateAsync(new SubagentSessionOptions(
                AgentName: "reviewer",
                ParentAgentName: "reviewer",
                SpawnPolicy: new SubagentSpawnPolicy(ParentSpawns: new HashSet<string>(["task"], StringComparer.Ordinal))),
                CancellationToken.None));
        Assert.Equal("self-recursion", blocked.Reason);

        var handle = await service.CreateAsync(new SubagentSessionOptions(
            AgentName: "reviewer",
            ParentAgentName: "reviewer",
            SpawnPolicy: new SubagentSpawnPolicy(ParentSpawns: new HashSet<string>(["reviewer"], StringComparer.Ordinal))),
            CancellationToken.None);
        Assert.NotNull(handle);
        Assert.Equal("reviewer", handle.AgentName);
        Assert.Equal("reviewer", handle.ParentAgentName);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncBlocksBeyondDepthCap()
    {
        var (service, _) = await CreateServiceAsync();

        var ex = await Assert.ThrowsAsync<SubagentSpawnBlockedException>(() =>
            service.CreateAsync(new SubagentSessionOptions(
                AgentName: "task",
                Depth: 2,
                SpawnPolicy: new SubagentSpawnPolicy(MaxRecursionDepth: 2)),
                CancellationToken.None));

        Assert.Equal("max-recursion-depth", ex.Reason);
    }

    [Fact]
    public async Task CreateAsyncAtDepthCapChildLosesSpawnTool()
    {
        var (service, _) = await CreateServiceAsync();
        var injected = new TestYieldTool();

        var handle = await service.CreateAsync(new SubagentSessionOptions(
            Tools: [injected],
            ActiveToolNames: ["task", "read"],
            AgentName: "task",
            Depth: 0,
            SpawnPolicy: new SubagentSpawnPolicy(MaxRecursionDepth: 1)), CancellationToken.None);

        Assert.NotNull(handle);
        Assert.Equal(1, handle.Depth);
        Assert.DoesNotContain(SubagentSessionService.SpawnToolName, handle.Harness.ActiveToolNames);
        Assert.Contains("read", handle.Harness.ActiveToolNames);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncBlocksSpawnNotInParentAllowlist()
    {
        var (service, _) = await CreateServiceAsync();

        var ex = await Assert.ThrowsAsync<SubagentSpawnBlockedException>(() =>
            service.CreateAsync(new SubagentSessionOptions(
                AgentName: "scout",
                SpawnPolicy: new SubagentSpawnPolicy(ParentSpawns: new HashSet<string>(["task"], StringComparer.Ordinal))),
                CancellationToken.None));

        Assert.Equal("not-allowed", ex.Reason);
    }

    [Fact]
    public async Task CreateAsyncAllowsSpawnInParentAllowlist()
    {
        var (service, _) = await CreateServiceAsync();

        var handle = await service.CreateAsync(new SubagentSessionOptions(
            AgentName: "task",
            SpawnPolicy: new SubagentSpawnPolicy(ParentSpawns: new HashSet<string>(["task"], StringComparer.Ordinal))),
            CancellationToken.None);

        Assert.NotNull(handle);
        Assert.Equal("task", handle.AgentName);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    // --- helpers ---

    private async Task<(SubagentSessionService Service, SessionRuntime Runtime)> CreateServiceAsync()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        return (new SubagentSessionService(runtime), runtime);
    }

    /// <summary>Scripted stream: first call is a terminating <c>yield</c> tool call; the loop must
    /// end there (Terminate), so the second branch is only a safety net.</summary>
    private static async IAsyncEnumerable<AssistantMessageEvent> YieldThenDoneStream(
        ModelDescriptor _,
        AgentContext context,
        AgentStreamOptions __,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ___ = default)
    {
        await Task.Yield();
        if (context.Messages.OfType<ToolResultMessage>().Any())
        {
            var done = AgentMessages.Assistant("done");
            yield return new AssistantMessageEvent.Start(done);
            yield return new AssistantMessageEvent.Done(done);
            yield break;
        }

        using var doc = JsonDocument.Parse("""{"data":{"findings":[{"severity":"warning","summary":"ok"}]}}""");
        var message = new AssistantMessage(
            [new ToolCallContent("call-yield", SubagentSessionService.YieldToolName, doc.RootElement.Clone())],
            StopReason: "tool_use");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    /// <summary>Minimal stand-in for the plugin's YieldTool: returns the submitted data as a
    /// terminating structured result under the <c>yield</c> tool name.</summary>
    private sealed class TestYieldTool : IAgentTool
    {
        public string Name => SubagentSessionService.YieldToolName;
        public string Label => "yield";
        public string Description => "Test yield tool.";
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;

        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(
            string toolCallId,
            JsonElement parameters,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback<object?>? onUpdate = null)
        {
            var data = parameters.TryGetProperty("data", out var dataProperty) ? dataProperty : parameters;
            return Task.FromResult(new AgentToolResult<object?>(
                [new TextContent(data.GetRawText())],
                data.Clone(),
                Terminate: true));
        }
    }
}
