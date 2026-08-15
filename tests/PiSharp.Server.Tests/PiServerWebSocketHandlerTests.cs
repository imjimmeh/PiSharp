using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.UiBridge;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.Tests;

public sealed class PiServerWebSocketHandlerTests
{
    [Fact]
    public async Task HttpHandshakeRejectsMissingApiKey()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var context = new DefaultHttpContext();

        await handler.HandleHttpAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnknownCommandReturnsFailureResponse()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));

        var response = await handler.DispatchTextCommandAsync("{\"id\":\"1\",\"type\":\"missing\"}");

        Assert.False(response.Success);
        Assert.Equal("unknown_command", response.Error?.Code);
    }

    [Fact]
    public async Task CreateSessionCommandCreatesRuntimeAndReturnsState()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var command = JsonSerializer.Serialize(new { id = "1", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        Assert.Single(registry.Sessions);
    }

    [Fact]
    public async Task ListSessionsCommandWithLiveSessionReturnsPersistedSessions()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        await live.Runtime.Session.Storage.AppendEntryAsync(UserEntry("live", "hello"));
        var listCommand = JsonSerializer.Serialize(new { id = "list", type = ServerCommandTypes.ListSessions, serverSessionId = created.ServerSessionId }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(listCommand);

        Assert.True(response.Success);
        var result = Assert.IsType<ServerSessionListResult>(response.Data);
        var session = Assert.Single(result.Sessions);
        Assert.Equal(created.State.RuntimeSessionId, session.Id);
        Assert.True(session.IsLive);
        Assert.Equal(created.ServerSessionId, session.ServerSessionId);
    }

    [Fact]
    public async Task ListSessionsCommandWithoutLiveSessionUsesCwdAndSessionsRoot()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var cwd = TempRoot();
        var sessionsRoot = Path.Combine(cwd, "custom-sessions");
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(cwd), sessionsRoot);
        var persisted = await repo.CreateAsync(new JsonlSessionCreateOptions(cwd, "persisted"));
        await persisted.Storage.AppendEntryAsync(UserEntry("persisted", "hello"));
        var metadata = persisted.Metadata;
        var command = JsonSerializer.Serialize(new { id = "list", type = ServerCommandTypes.ListSessions, cwd, sessionsRoot }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var result = Assert.IsType<ServerSessionListResult>(response.Data);
        var session = Assert.Single(result.Sessions);
        Assert.Equal(metadata.Id, session.Id);
        Assert.False(session.IsLive);
        Assert.Null(session.ServerSessionId);
    }

    [Fact]
    public async Task ListSessionsCommandWithoutLiveSessionRequiresCwd()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var command = JsonSerializer.Serialize(new { id = "list", type = ServerCommandTypes.ListSessions }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
        Assert.Contains("cwd is required", response.Error?.Message);
    }

    [Fact]
    public async Task AttachCommand_ReplaysFromSinceSequence()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));

        await EmitTurnAsync(live);
        var head = live.EventLog.HeadSequence;

        var attachResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "a", type = ServerCommandTypes.Attach, serverSessionId = created.ServerSessionId, sinceSequence = head - 1
        }, ServerJsonSerializer.Options));

        Assert.True(attachResponse.Success);
        var result = Assert.IsType<AttachResult>(attachResponse.Data);
        Assert.Equal(head, result.HeadSequence);
        Assert.False(result.Gap);
    }

    [Fact]
    public async Task AttachCommand_WithOldSinceSequence_ReportsGap()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));

        await EmitTurnAsync(live);
        var head = live.EventLog.HeadSequence;
        var sample = live.EventLog.ReplayFrom(1).Events[0];
        live.EventLog.Append(sample with { Sequence = head + 1000 });

        var attachResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "a", type = ServerCommandTypes.Attach, serverSessionId = created.ServerSessionId, sinceSequence = 1
        }, ServerJsonSerializer.Options));

        Assert.True(attachResponse.Success);
        var result = Assert.IsType<AttachResult>(attachResponse.Data);
        Assert.True(result.Gap);
        Assert.Equal(head + 1000, result.HeadSequence);
    }

    [Fact]
    public async Task AttachCommand_UnknownSession_ReturnsFailure()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var command = JsonSerializer.Serialize(new { id = "a", type = ServerCommandTypes.Attach, serverSessionId = "missing", sinceSequence = 0 }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    [Fact]
    public async Task GetStateCommand_ReturnsHeadSequence()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));

        await EmitTurnAsync(live);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "s", type = ServerCommandTypes.GetState, serverSessionId = created.ServerSessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var state = Assert.IsType<ServerSessionState>(response.Data);
        Assert.Equal(live.EventLog.HeadSequence, state.HighWatermark);
    }

    [Fact]
    public async Task GetStateCommand_WithoutSession_Fails()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var command = JsonSerializer.Serialize(new { id = "s", type = ServerCommandTypes.GetState, serverSessionId = "missing" }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
    }

    [Fact]
    public async Task RunCommand_InvokesRegisteredDelegate()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry);
        var invoked = false;
        var delegates = new PiServerCommandDelegates(
            RunCommandAsync: (context, text, options, ct) =>
            {
                invoked = true;
                Assert.Equal("/help", text);
                Assert.Same(registry.Sessions.Single(), context.Session);
                Assert.Same(bridge, context.UiBridge);
                return Task.FromResult(new ServerCommandResult(true, "done"));
            });
        var handler = CreateHandler(registry, bridge, delegates);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.RunCommand, serverSessionId = created.ServerSessionId, text = "/help"
        }, ServerJsonSerializer.Options));

        Assert.True(invoked);
        Assert.True(response.Success);
        var result = Assert.IsType<ServerCommandResult>(response.Data);
        Assert.Equal("done", result.Message);
    }

    [Fact]
    public async Task UiResponse_CompletesPendingUiRequest()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry);
        var handler = CreateHandler(registry, bridge);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var intent = new ServerUiIntent("req-1", "select", "Pick", "Choose one", ["a", "b"], null);
        var requestTask = bridge.RequestUiAsync(intent);

        await Task.Yield();
        Assert.False(requestTask.IsCompleted);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "u", type = ServerCommandTypes.UiResponse, serverSessionId = created.ServerSessionId,
            requestId = "req-1", value = "a", cancelled = false
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var uiResponse = await requestTask;
        Assert.False(uiResponse.Cancelled);
        Assert.Equal("a", uiResponse.Value);
    }

    [Fact]
    public async Task CommandWithAbsentDelegate_ReturnsNotAvailable()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "x", type = ServerCommandTypes.CompleteCommand, serverSessionId = created.ServerSessionId, text = "/he"
        }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("not_available", response.Error?.Code);
    }

    [Fact]
    public async Task GetExtensionRegistry_SerializesWireMappingOfRegisteredExtensions()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeWithExtensionsAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var response = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.GetExtensionRegistry, serverSessionId = created.ServerSessionId
        }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var wire = Assert.IsType<ExtensionRegistryWire>(response.Data);
        var tool = Assert.Single(wire.Tools);
        Assert.Equal("fmt", tool.Name);
        Assert.Equal("Format", tool.Label);
        Assert.Equal("Pretty-prints code", tool.Description);
        Assert.Equal(ToolExecutionMode.Parallel, tool.ExecutionMode);
        Assert.Equal("Use fmt", tool.PromptSnippet);
        Assert.Contains("guideline", tool.PromptGuidelines!);
        Assert.Equal(ExtensionChatRowType.Custom.ToString(), Assert.Single(wire.Renderers).RowType);
        Assert.Equal("my-type", Assert.Single(wire.Renderers).CustomType);
        Assert.Equal(ExtensionOverridePolicy.OverrideBuiltIn, Assert.Single(wire.Renderers).Override);
        Assert.Equal(ExtensionChatRowType.System.ToString(), Assert.Single(wire.Decorators).RowType);
        Assert.Equal("ctrl+r", Assert.Single(wire.Shortcuts).Keys);
    }
    [Fact]
    public async Task ResolveTool_AnswersExtensionToolWire()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeWithExtensionsAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var command = JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.ResolveTool, serverSessionId = created.ServerSessionId, name = "fmt"
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var wire = Assert.IsType<ExtensionToolWire>(response.Data);
        Assert.Equal("fmt", wire.Name);
        Assert.Equal("Format", wire.Label);
        Assert.Equal("Pretty-prints code", wire.Description);
        Assert.Equal(ToolExecutionMode.Parallel, wire.ExecutionMode);
        Assert.Equal("Use fmt", wire.PromptSnippet);
        Assert.False(wire.HasRenderCall);
        Assert.False(wire.HasRenderResult);
    }

    [Fact]
    public async Task ResolveTool_UnknownTool_ReturnsNotAvailable()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeWithExtensionsAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var command = JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.ResolveTool, serverSessionId = created.ServerSessionId, name = "nope"
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("not_available", response.Error?.Code);
    }

    [Fact]
    public async Task RenderToolCall_ForRenderableTool_ReturnsRenderedLines()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeWithRenderableToolAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var command = JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.RenderToolCall, serverSessionId = created.ServerSessionId,
            name = "fmt-render", toolCallId = "tc-1", arguments = new { file = "a.cs" },
            isCall = true, isError = false, isExpanded = false, width = 120
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var payload = JsonSerializer.Deserialize<RenderLinesPayload>(
            JsonSerializer.Serialize(response.Data, ServerJsonSerializer.Options), ServerJsonSerializer.Options);
        Assert.Equal(["formatted call"], payload?.Lines);
    }

    [Fact]
    public async Task RenderToolResult_ForRenderableTool_ReturnsRenderedLines()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeWithRenderableToolAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var command = JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.RenderToolResult, serverSessionId = created.ServerSessionId,
            name = "fmt-render", toolCallId = "tc-1", arguments = new { file = "a.cs" },
            isCall = false, isError = true, isExpanded = true, width = 120
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var payload = JsonSerializer.Deserialize<RenderLinesPayload>(
            JsonSerializer.Serialize(response.Data, ServerJsonSerializer.Options), ServerJsonSerializer.Options);
        Assert.Equal(["formatted result"], payload?.Lines);
    }

    [Fact]
    public async Task RenderToolCall_UnknownTool_ReturnsNotAvailable()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeWithExtensionsAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);

        var command = JsonSerializer.Serialize(new
        {
            id = "r", type = ServerCommandTypes.RenderToolCall, serverSessionId = created.ServerSessionId,
            name = "nope", toolCallId = "tc-1", arguments = new { },
            isCall = true, isError = false, isExpanded = false, width = 120
        }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("not_available", response.Error?.Code);
    }

    private sealed record RenderLinesPayload(IReadOnlyList<string>? Lines);

    /// <summary>Stub tool that renders its own call/result lines (mirrors TS-bridge renderable tools).</summary>
    private sealed class RenderableStubTool : IAgentTool, IAgentToolRenderer
    {
        private static readonly JsonDocument Schema = JsonDocument.Parse("{\"type\":\"object\"}");

        public string Name => "fmt-render";
        public string Label => "Format";
        public string Description => "Pretty-prints code";
        public JsonElement ParametersSchema => Schema.RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => ToolExecutionMode.Parallel;
        public bool HasRenderCall => true;
        public bool HasRenderResult => true;

        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(
            string toolCallId,
            JsonElement parameters,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback<object?>? onUpdate = null)
            => Task.FromResult(new AgentToolResult<object?>([], null));

        public Task<ToolRenderResult?> RenderCallAsync(ToolRenderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<ToolRenderResult?>(new ToolRenderResult(["formatted call"]));

        public Task<ToolRenderResult?> RenderResultAsync(ToolRenderRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<ToolRenderResult?>(new ToolRenderResult(["formatted result"]));
    }

    private static PiServerWebSocketHandler CreateHandler(ServerSessionRegistry registry, IServerUiBridge? uiBridge = null, PiServerCommandDelegates? delegates = null)
        => new(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance, uiBridge, delegates);

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])), initial);
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeWithExtensionsAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var extensions = new ExtensionRegistry();
        extensions.RegisterTool(
            "ext:test",
            new ExtensionToolRegistration(
                "fmt", "Format", "Pretty-prints code",
                JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
                (_, _, _, _) => Task.FromResult(new AgentToolResult<object?>([], null)),
                ExecutionMode: ToolExecutionMode.Parallel,
                PromptSnippet: "Use fmt",
                PromptGuidelines: ["guideline"]).ToAgentTool());
        extensions.RegisterShortcut("ext:test", new ExtensionShortcutRegistration("ctrl+r", "Runs something", (_, _) => Task.CompletedTask));
        extensions.RegisterMessageRenderer("ext:test", new ExtensionMessageRendererRegistration(
            "custom-ren", RowType: ExtensionChatRowType.Custom, CustomType: "my-type", Override: ExtensionOverridePolicy.OverrideBuiltIn));
        extensions.RegisterMessageDecorator("ext:test", new ExtensionMessageDecoratorRegistration(
            "custom-dec", RowType: ExtensionChatRowType.System, CustomType: "dec-type"));
        return new PiSharp.Runtime.SessionRuntime(
            repo,
            createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])),
            initial,
            new ExtensionManager(extensions));
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeWithRenderableToolAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var extensions = new ExtensionRegistry();
        extensions.RegisterTool("ext:test", new RenderableStubTool());
        return new PiSharp.Runtime.SessionRuntime(
            repo,
            createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])),
            initial,
            new ExtensionManager(extensions));
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static MessageEntry UserEntry(string id, string text)
        => new() { Id = id, ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User(text) };

    private static Task EmitTurnAsync(LiveServerSession live)
        => live.RunExclusiveAsync((runtime, _) => runtime.Harness.PromptAsync("hi", [], CancellationToken.None));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ws-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
