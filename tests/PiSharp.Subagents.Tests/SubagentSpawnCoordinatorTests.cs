using System.Reflection;
using System.Text.Json;
using PiSharp.Abstractions.Sessions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using PiSharp.Runtime.Subagents;
using PiSharp.Subagents.AgentDefinitions;
using PiSharp.Subagents.Discovery;
using PiSharp.Subagents.Spawning;
using PiSharp.Subagents.Tools;
using Xunit;

namespace PiSharp.Subagents.Tests;

public sealed class SubagentSpawnCoordinatorTests : IAsyncLifetime
{
    private string _tempRoot = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pisharp-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
        return Task.CompletedTask;
    }

    private string TempDir() => Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));

    private const string ReviewerDefinition = """
        ---
        name: reviewer
        description: Evidence-backed reviewer.
        tools: [read, grep, glob, yield]
        spawns: []
        output:
          type: object
          required: [findings]
          properties:
            findings:
              type: array
              minItems: 1
              items:
                type: object
                required: [severity, summary]
                properties:
                  severity: { type: string, enum: [critical, warning, info] }
                  summary: { type: string }
                additionalProperties: false
        ---

        You are a reviewer. Finish with `yield`.
        """;

    private static AgentDefinitionRegistry BuildRegistry(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "reviewer.md"), ReviewerDefinition);
        var registry = new AgentDefinitionRegistry();
        registry.Replace(new AgentDefinitionDiscovery(projectDirs: [projectDir]).Discover());
        return registry;
    }

    // --- Pure policy decisions (no runtime needed) ---

    [Fact]
    public async Task SpawnAsyncBlocksUnknownAgent()
    {
        var registry = new AgentDefinitionRegistry();
        registry.Replace(new Dictionary<string, AgentDefinition>(StringComparer.Ordinal));
        var coordinator = new SubagentSpawnCoordinator(registry, SubagentSettings.Default);

        var outcome = await coordinator.SpawnAsync(new TaskToolInput("nope", "do it"), "call-1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("unknown-agent", outcome.BlockReason);
    }

    [Fact]
    public async Task SpawnAsyncBlocksDisabledAgent()
    {
        var registry = BuildRegistry(TempDir());
        var settings = SubagentSettings.Default with
        {
            DisabledAgents = new HashSet<string>(["reviewer"], StringComparer.Ordinal),
        };
        registry.Replace(registry.All, new HashSet<string>(["reviewer"], StringComparer.Ordinal));
        var coordinator = new SubagentSpawnCoordinator(registry, settings);

        var outcome = await coordinator.SpawnAsync(new TaskToolInput("reviewer", "do it"), "call-1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("disabled", outcome.BlockReason);
    }

    [Fact]
    public async Task SpawnAsyncBlocksSelfRecursion()
    {
        var registry = BuildRegistry(TempDir());
        var coordinator = new SubagentSpawnCoordinator(
            registry,
            SubagentSettings.Default,
            parentAgentName: "reviewer",
            parentSpawns: ["task"]);

        var outcome = await coordinator.SpawnAsync(new TaskToolInput("reviewer", "review yourself"), "call-1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("self-recursion", outcome.BlockReason);
    }

    [Fact]
    public async Task SpawnAsyncBlocksBeyondDepthCap()
    {
        var registry = BuildRegistry(TempDir());
        var coordinator = new SubagentSpawnCoordinator(
            registry,
            SubagentSettings.Default with { MaxRecursionDepth = 2 },
            depth: 2);

        var outcome = await coordinator.SpawnAsync(new TaskToolInput("reviewer", "do it"), "call-1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("max-recursion-depth", outcome.BlockReason);
    }

    [Fact]
    public async Task SpawnAsyncBlocksAgentNotInParentSpawnsAllowlist()
    {
        var registry = BuildRegistry(TempDir());
        var coordinator = new SubagentSpawnCoordinator(
            registry,
            SubagentSettings.Default,
            parentSpawns: ["scout"]);

        var outcome = await coordinator.SpawnAsync(new TaskToolInput("reviewer", "do it"), "call-1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("not-allowed", outcome.BlockReason);
    }

    [Fact]
    public async Task SpawnAsyncFailsWhenNoServiceAvailable()
    {
        var registry = BuildRegistry(TempDir());
        var coordinator = new SubagentSpawnCoordinator(registry, SubagentSettings.Default, service: null);

        // Deterministically clear the ambient registration (tests within the class may run in any order).
        var ambientField = typeof(SubagentRuntimeAccess).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = ambientField.GetValue(null);
        try
        {
            ambientField.SetValue(null, null);

            var outcome = await coordinator.SpawnAsync(new TaskToolInput("reviewer", "do it"), "call-1", CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("not available", outcome.Error, StringComparison.Ordinal);
        }
        finally
        {
            ambientField.SetValue(null, previous);
        }
    }

    // --- Plan-mode hook (P14 surface) ---

    [Fact]
    public void ApplyPlanModeClearsSpawnsAndRestrictsTools()
    {
        var definition = AgentDefinitionParser.Parse(ReviewerDefinition, "reviewer.md", AgentSourceKind.Project).Definition!;

        var policy = SubagentSpawnCoordinator.ApplyPlanMode(definition);

        Assert.Empty(policy["spawns"]);
        Assert.Equal(["read", "grep", "glob", "yield"], policy["tools"]);
        Assert.Contains(SubagentSessionService.YieldToolName, policy["tools"]);
    }

    // --- End-to-end in-process fan-out ---

    [Fact]
    public async Task SpawnAsyncReturnsStructuredResultDirectly()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var parentId = initial.Metadata.Id;
        var runtime = new SessionRuntime(repo, createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(
                new AgentHarnessOptions<JsonlSessionMetadata>(
                    session,
                    new ModelDescriptor("test", "test", "test"),
                    session.Metadata.Id == parentId ? ParentFanOutStream : ChildYieldStream,
                    FakeCompletion,
                    [])),
            initial);
        var service = new SubagentSessionService(runtime);
        var registry = BuildRegistry(TempDir());
        var coordinator = new SubagentSpawnCoordinator(registry, SubagentSettings.Default, service);

        var outcome = await coordinator.SpawnAsync(new TaskToolInput("reviewer", "review the change"), "call-1", CancellationToken.None);

        Assert.True(outcome.Success, $"outcome: success={outcome.Success} block={outcome.BlockReason} error={outcome.Error}");
        Assert.NotNull(outcome.StructuredResult);
        Assert.Equal("warning", outcome.StructuredResult!.Value.GetProperty("findings")[0].GetProperty("severity").GetString());

        await service.DisposeAllAsync(CancellationToken.None);
        await runtime.DisposeAsync();
    }

    public async Task SpawnAsyncRunsChildThatYieldsTypedStructuredResultAndEmitsEvents()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var parentId = initial.Metadata.Id;
        var runtime = new SessionRuntime(repo, createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(
                new AgentHarnessOptions<JsonlSessionMetadata>(
                    session,
                    new ModelDescriptor("test", "test", "test"),
                    session.Metadata.Id == parentId ? ParentFanOutStream : ChildYieldStream,
                    FakeCompletion,
                    [])),
            initial);
        var service = new SubagentSessionService(runtime);
        var emitted = new List<(string Name, object? Payload)>();
        var registry = BuildRegistry(TempDir());
        var coordinator = new SubagentSpawnCoordinator(
            registry,
            SubagentSettings.Default,
            service,
            emitEvent: (name, payload, _) => { emitted.Add((name, payload)); return Task.CompletedTask; });

        runtime.Harness.RegisterTool("test", new TaskTool(coordinator));
        var result = await runtime.Harness.PromptAsync("fan out", CancellationToken.None);

        Assert.Contains(result.Content.OfType<TextContent>(), text => text.Text.Contains("parent summary", StringComparison.Ordinal));

        var parentContext = await runtime.Session.BuildContextAsync(CancellationToken.None);
        var taskResult = Assert.Single(parentContext.Messages.OfType<ToolResultMessage>().Where(m => m.ToolName == "task"));
        var details = Assert.IsType<JsonElement>(taskResult.Details);
        Assert.Equal("warning", details.GetProperty("findings")[0].GetProperty("severity").GetString());

        Assert.Contains(emitted, item => item.Name == SubagentEventNames.Created && ((SubagentCreatedEvent)item.Payload!).Agent == "reviewer");
        Assert.Contains(emitted, item => item.Name == SubagentEventNames.Started);
        var completed = Assert.Single(emitted.Where(item => item.Name == SubagentEventNames.Completed));
        var completedPayload = Assert.IsType<SubagentCompletedEvent>(completed.Payload);
        Assert.Equal("completed", completedPayload.Status);
        Assert.NotNull(completedPayload.StructuredResult);
        Assert.Equal("warning", completedPayload.StructuredResult!.Value.GetProperty("findings")[0].GetProperty("severity").GetString());

        await service.DisposeAllAsync(CancellationToken.None);
        await runtime.DisposeAsync();
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> ParentFanOutStream(
        ModelDescriptor _,
        AgentContext context,
        AgentStreamOptions __,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ___ = default)
    {
        await Task.Yield();
        if (context.Messages.OfType<ToolResultMessage>().Any())
        {
            var message = AgentMessages.Assistant("parent summary");
            yield return new AssistantMessageEvent.Start(message);
            yield return new AssistantMessageEvent.Done(message);
            yield break;
        }

        using var doc = JsonDocument.Parse("""{"agent":"reviewer","task":"review the change"}""");
        var toolCall = new AssistantMessage(
            [new ToolCallContent("call-task", "task", doc.RootElement.Clone())],
            StopReason: "tool_use");
        yield return new AssistantMessageEvent.Start(toolCall);
        yield return new AssistantMessageEvent.Done(toolCall);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> ChildYieldStream(
        ModelDescriptor _,
        AgentContext context,
        AgentStreamOptions __,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ___ = default)
    {
        await Task.Yield();
        using var doc = JsonDocument.Parse("""{"data":{"findings":[{"severity":"warning","summary":"reviewed"}]}}""");
        var message = new AssistantMessage(
            [new ToolCallContent("call-yield", SubagentSessionService.YieldToolName, doc.RootElement.Clone())],
            StopReason: "tool_use");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
