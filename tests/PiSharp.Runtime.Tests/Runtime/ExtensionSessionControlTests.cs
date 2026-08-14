using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Runtime.Tests;

/// <summary>
using PiSharp.Runtime.IO;
/// the replacement session) and the P15 eval loopback
/// (<see cref="IExtensionToolApi.ExecuteToolAsync"/> routed through
/// <see cref="SessionRuntime.Tools"/> and registry tools).
/// </summary>
public sealed class ExtensionSessionControlTests
{
    [Fact]
    public async Task NewSessionAsync_InvokesWithSessionWithReplacementApi()
    {
        var (runtime, _) = await CreateRuntimeAsync(extensionManager: null);
        runtime.BindExtensionRuntime();

        var oldSessionId = runtime.Session.Metadata.Id;
        IExtensionReplacementSessionApi? observed = null;
        var result = await runtime.ExtensionBinding.Session.NewSessionAsync(
            (replacement, _) => { observed = replacement; return Task.CompletedTask; });

        Assert.False(result.Cancelled);
        Assert.NotNull(result.SessionId);
        Assert.NotEqual(oldSessionId, result.SessionId);
        Assert.NotNull(observed);
        Assert.Equal(result.SessionId, observed!.SessionId);
        Assert.NotEqual(runtime.Session.Metadata.Id, oldSessionId);
    }

    [Fact]
    public async Task NewSessionAsync_ReplacementSendRoutesToNewSession()
    {
        var (runtime, _) = await CreateRuntimeAsync(extensionManager: null);
        runtime.BindExtensionRuntime();

        var oldSessionId = runtime.Session.Metadata.Id;
        var postedTo = new List<string>();
        await runtime.ExtensionBinding.Session.NewSessionAsync(async (replacement, _) =>
        {
            await replacement.SendMessageAsync(AgentMessages.User("after-switch"), ExtensionMessageDelivery.NextTurn, triggerTurn: false);
            postedTo.Add(replacement.SessionId!);
        });

        Assert.NotEqual(oldSessionId, runtime.Session.Metadata.Id);
        var message = Assert.Single(postedTo);
        Assert.Equal(runtime.Session.Metadata.Id, message);
    }

    [Fact]
    public async Task SwitchSessionAsync_MissingSessionIsCancelledAndSkipsCallback()
    {
        var (runtime, _) = await CreateRuntimeAsync(extensionManager: null);
        runtime.BindExtensionRuntime();

        var invoked = false;
        var result = await runtime.ExtensionBinding.Session.SwitchSessionAsync(
            "does-not-exist",
            (_, _) => { invoked = true; return Task.CompletedTask; });

        Assert.True(result.Cancelled);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ForkAsync_InvokesWithSessionCallback()
    {
        var (runtime, _) = await CreateRuntimeAsync(extensionManager: null);
        runtime.BindExtensionRuntime();

        var invoked = false;
        var result = await runtime.ExtensionBinding.Session.ForkAsync(
            entryId: null,
            withSession: (_, _) => { invoked = true; return Task.CompletedTask; });

        Assert.False(result.Cancelled);
        Assert.True(invoked);
        Assert.NotNull(result.SessionId);
    }

    [Fact]
    public async Task ExecuteToolAsync_DispatchesToRegistryToolByName()
    {
        var registry = new ExtensionRegistry();
        var (runtime, _) = await CreateRuntimeAsync(new ExtensionManager(registry));
        runtime.BindExtensionRuntime();

        registry.RegisterTool("extension:test", new EchoTool("extension_dynamic"));

        var result = await runtime.ExtensionBinding.Tools.ExecuteToolAsync("extension_dynamic", JsonDocument.Parse("{}").RootElement);

        Assert.Contains("extension_dynamic result", result.Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public async Task ExecuteToolAsync_UnknownToolReturnsErrorText()
    {
        var (runtime, _) = await CreateRuntimeAsync(extensionManager: null);
        runtime.BindExtensionRuntime();

        var result = await runtime.ExtensionBinding.Tools.ExecuteToolAsync("missing_tool", JsonDocument.Parse("{}").RootElement);

        Assert.Contains("was not found", result.Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public async Task ExecuteToolAsync_DispatchesToRuntimeSeedTools()
    {
        var (runtime, _) = await CreateRuntimeAsync(extensionManager: null, tools: [new EchoTool("seeded_tool")]);
        runtime.BindExtensionRuntime();

        var result = await runtime.ExtensionBinding.Tools.ExecuteToolAsync("seeded_tool", JsonDocument.Parse("{}").RootElement);

        Assert.Contains("seeded_tool result", result.Content.OfType<TextContent>().Single().Text);
    }

    private static async Task<(SessionRuntime Runtime, string Root)> CreateRuntimeAsync(ExtensionManager? extensionManager = null, IReadOnlyList<IAgentTool>? tools = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-spine-session-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: extensionManager, tools: tools);
        return (runtime, root);
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(AgentMessages.Assistant("ok"));
    }

    private sealed class EchoTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;
        public JsonElement PrepareArguments(JsonElement args) => args;
        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
            => Task.FromResult(new AgentToolResult<object?>([new TextContent($"{name} result")], new { name }));
    }
}
