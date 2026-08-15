using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Agent.Harness;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;
using PiSharp.Tui.Interactive;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class RemoteTuiBackendTests
{
    private const string SessionId = "srv-test";
    private static readonly ModelDescriptor TestModel = new("openai", "gpt-test", "openai", Name: "GPT Test", ContextWindow: 12345);

    [Fact]
    public async Task Subscribe_ForwardsMessageEventToListener()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var received = new ConcurrentQueue<AgentHarnessEvent>();
        backend.Subscribe((evt, _) =>
        {
            received.Enqueue(evt);
            return Task.CompletedTask;
        });

        var message = new UserMessage([new TextContent("hello daemon")]);
        transport.Events.Writer.TryWrite(ServerEventEnvelope.FromFlat(
            SessionId, 1, AgentSessionEvent.FromCore(new AgentEvent.MessageStart(message))));

        await WaitUntilAsync(() => received.Count == 1);
        var harnessEvent = Assert.IsType<AgentHarnessEvent.Core>(received.Single());
        var start = Assert.IsType<AgentEvent.MessageStart>(harnessEvent.Event);
        var userMessage = Assert.IsType<UserMessage>(start.Message);
        Assert.Equal("hello daemon", Assert.IsType<TextContent>(userMessage.Content[0]).Text);
    }

    [Fact]
    public async Task PromptAsync_SendsPromptCommand()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var images = new List<ImageContent> { new("image/png", "aGVsbG8=") };
        await backend.PromptAsync("hello", images, CancellationToken.None);

        var (envelope, payload) = Assert.Single(transport.Commands);
        Assert.Equal(ServerCommandTypes.Prompt, envelope.Type);
        Assert.Equal("hello", (string?)PayloadValue(payload, "message"));
        Assert.Same(images, (IReadOnlyList<ImageContent>?)PayloadValue(payload, "images"));
    }

    [Fact]
    public async Task Abort_SendsAbortCommand()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        backend.Abort();
        await WaitUntilAsync(() => transport.Commands.Count == 1);

        var (envelope, payload) = Assert.Single(transport.Commands);
        Assert.Equal(ServerCommandTypes.Abort, envelope.Type);
        Assert.Null(payload);
        Assert.Equal(SessionId, envelope.ServerSessionId);
    }

    [Fact]
    public async Task GetSessionSnapshot_ReturnsMappedSnapshot()
    {
        var entry = new MessageEntry
        {
            Id = "e1",
            ParentId = null,
            Timestamp = DateTimeOffset.UtcNow,
            Message = new UserMessage([new TextContent("hi")]),
        };
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.GetSessionSnapshot
                ? ServerResponse.Ok("st", type, new ServerSessionSnapshot("s1", "/sess.jsonl", "Name", [entry]))
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var snapshot = await backend.GetSessionSnapshotAsync(CancellationToken.None);

        Assert.Equal("s1", snapshot.SessionId);
        Assert.Equal("Name", snapshot.SessionName);
        var parsed = Assert.IsType<MessageEntry>(Assert.Single(snapshot.BranchEntries));
        Assert.Equal("e1", parsed.Id);
    }


    [Fact]
    public async Task GetSessionName_DeserializesWireStateWithStringThinkingLevel()
    {
        var state = new ServerSessionState(
            SessionId, "rt-1", "/s.jsonl", "Session", "/cwd", TestModel, ThinkingLevel.Off,
            IsBusy: false, IsCompacting: false, MessageCount: 0);
        using var document = JsonDocument.Parse(ServerJsonSerializer.Serialize(state));
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.GetState
                ? ServerResponse.Ok("st", type, document.RootElement.Clone())
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var sessionName = await backend.GetSessionNameAsync(CancellationToken.None);

        Assert.Equal("Session", sessionName);
    }
    [Fact]
    public async Task GapInSequence_TriggersGetStateAndAttach()
    {
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.GetState
                ? ServerResponse.Ok("st", type, new ServerSessionState(
                    SessionId, "rt-1", "/s.jsonl", "Sess", "/cwd", TestModel, ThinkingLevel.Medium,
                    IsBusy: false, IsCompacting: false, MessageCount: 0, HighWatermark: 8))
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };
        var resynced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Resynced += () => resynced.TrySetResult();

        transport.Events.Writer.TryWrite(MessageEnvelope(5, "first"));
        transport.Events.Writer.TryWrite(MessageEnvelope(8, "gapped"));
        await resynced.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var commands = transport.Commands.ToArray();
        Assert.Contains(commands, command => command.Envelope.Type == ServerCommandTypes.GetState);
        var attach = Assert.Single(commands, command => command.Envelope.Type == ServerCommandTypes.Attach);
        Assert.Equal(5L, (long?)PayloadValue(attach.Payload, "sinceSequence"));
    }

    [Fact]
    public async Task ModelAndPhase_DerivedFromState()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        transport.Events.Writer.TryWrite(OwnEnvelope(1, new AgentHarnessOwnEvent.ModelSelect(TestModel, null, "test")));
        transport.Events.Writer.TryWrite(OwnEnvelope(2, new AgentHarnessOwnEvent.ThinkingLevelChanged(ThinkingLevel.Medium)));
        transport.Events.Writer.TryWrite(CoreEnvelope(3, new AgentEvent.AgentStart()));

        await WaitUntilAsync(
            () => backend.Model.Id == TestModel.Id && backend.ThinkingLevel == ThinkingLevel.Medium && backend.Phase == AgentHarnessPhase.Turn);

        Assert.Equal(TestModel.Name, backend.Model.Name);
        Assert.Equal(ThinkingLevel.Medium, backend.ThinkingLevel);
        Assert.Equal(AgentHarnessPhase.Turn, backend.Phase);
    }

    [Fact]
    public async Task UiRequest_AutoCancelled_WhenNoHandler()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var intent = new ServerUiIntent("r1", "notify", "Title", "message", null, null);
        transport.Events.Writer.TryWrite(ServerEventEnvelope.FromFlat(
            SessionId, 1, AgentSessionEvent.FromServer("ui_request", intent)));

        await WaitUntilAsync(() => transport.Commands.Any(command => command.Envelope.Type == ServerCommandTypes.UiResponse));
        var (_, payload) = Assert.Single(transport.Commands, command => command.Envelope.Type == ServerCommandTypes.UiResponse);
        Assert.Equal("r1", (string?)PayloadValue(payload, "requestId"));
        Assert.Equal(true, (bool?)PayloadValue(payload, "cancelled"));
    }

    [Fact]
    public async Task LateRunCommandWithShouldExit_RaisesLateCommandShouldExit()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.LateCommandShouldExit += () => fired.TrySetResult();

        transport.Late.Writer.TryWrite(ServerResponse.Ok(
            "late-1", ServerCommandTypes.RunCommand, new ServerCommandResult(Handled: true, ShouldExit: true)));

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LateResponseWithoutShouldExit_DoesNotRaiseEvent()
    {
        var transport = new BackendFakeTransport();
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.LateCommandShouldExit += () => fired.TrySetResult();

        // A run_command that did not exit, and a ShouldExit response for a different command
        // type, must both stay silent on the event.
        transport.Late.Writer.TryWrite(ServerResponse.Ok(
            "late-1", ServerCommandTypes.RunCommand, new ServerCommandResult(Handled: true, ShouldExit: false)));
        transport.Late.Writer.TryWrite(ServerResponse.Ok(
            "late-2", ServerCommandTypes.CreateSession, new ServerCommandResult(Handled: true, ShouldExit: true)));

        await Task.Delay(300);
        Assert.False(fired.Task.IsCompleted);
    }
    [Fact]
    public async Task GetExtensionRegistry_ReconstructsRegistryFromWire()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionRegistryWire(
            Tools: [new ExtensionToolWire(
                "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
                HasRenderCall: false, HasRenderResult: false,
                RendererName: "my-renderer", RenderShell: "bash",
                ExecutionMode: null, PromptSnippet: "Use fmt", PromptGuidelines: ["guideline"])],
            Shortcuts: [new ExtensionShortcutWire("shortcut:ctrl+r:ext:test", "ext:test", "ctrl+r", "Runs something")],
            Renderers: [],
            Decorators: []);
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.GetExtensionRegistry
                ? ServerResponse.Ok("st", type, wire)
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var registry = await backend.GetExtensionRegistryAsync(CancellationToken.None);

        Assert.NotNull(registry);
        var tool = Assert.Single(registry.Tools);
        Assert.Equal("fmt", tool.Value.Name);
        Assert.Equal("Pretty-prints code", tool.Value.Description);
        Assert.Equal("Use fmt", tool.Value.PromptSnippet);
        var shortcut = Assert.Single(registry.Shortcuts);
        Assert.Equal("ctrl+r", shortcut.Value.Keys);
    }
    [Fact]
    public async Task GetExtensionShortcuts_BuildsHandlersThatInvokeOverTheWire()
    {
        var wire = new[]
        {
            new ExtensionShortcutWire("shortcut:ctrl+r:ext:test", "ext:test", "ctrl+r", "Runs something"),
        };
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.GetExtensionShortcuts
                ? ServerResponse.Ok("st", type, wire)
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var shortcuts = await backend.GetExtensionShortcutsAsync(CancellationToken.None);

        var shortcut = Assert.Single(shortcuts);
        Assert.Equal("ext:test", shortcut.SourceId);
        Assert.Equal("ctrl+r", shortcut.Value.Keys);
        await shortcut.Value.Handler("go", CancellationToken.None);

        var sent = Assert.Single(transport.Commands.Where(command => command.Envelope.Type == ServerCommandTypes.InvokeExtensionShortcut));
        Assert.Equal(ServerCommandTypes.InvokeExtensionShortcut, sent.Envelope.Type);
        Assert.Equal(SessionId, sent.Envelope.ServerSessionId);
        Assert.Equal("ctrl+r", PayloadValue(sent.Payload, "keys"));
        Assert.Equal("go", PayloadValue(sent.Payload, "args"));
    }

    [Fact]
    public async Task ExtensionShortcutHandler_ThrowsOnFailedInvokeResponse()
    {
        var wire = new[]
        {
            new ExtensionShortcutWire("shortcut:ctrl+r:ext:test", "ext:test", "ctrl+r", "Runs something"),
        };
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.GetExtensionShortcuts
                ? ServerResponse.Ok("st", type, wire)
                : ServerResponse.Fail("st", type, "not_available", "Extension shortcut 'ctrl+r' is not registered."),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var shortcuts = await backend.GetExtensionShortcutsAsync(CancellationToken.None);

        var shortcut = Assert.Single(shortcuts);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shortcut.Value.Handler("go", CancellationToken.None));
        Assert.Contains("not_available", exception.Message);
        Assert.Contains("ctrl+r", exception.Message);
    }


    [Fact]
    public async Task ResolveTool_ReturnsRemoteRegisteredTool_WithWireCapabilities()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionToolWire(
            "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
            HasRenderCall: true, HasRenderResult: false,
            RendererName: null, RenderShell: null,
            ExecutionMode: ToolExecutionMode.Parallel, PromptSnippet: "Use fmt", PromptGuidelines: ["guideline"]);
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.ResolveTool
                ? ServerResponse.Ok("st", type, wire)
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var tool = await backend.ResolveToolAsync("fmt", CancellationToken.None);

        Assert.NotNull(tool);
        Assert.Equal("fmt", tool.Name);
        Assert.Equal("Format", tool.Label);
        var renderer = Assert.IsAssignableFrom<IAgentToolRenderer>(tool);
        Assert.True(renderer.HasRenderCall);
        Assert.False(renderer.HasRenderResult);
        var (envelope, payload) = Assert.Single(transport.Commands);
        Assert.Equal(ServerCommandTypes.ResolveTool, envelope.Type);
        Assert.Equal(SessionId, envelope.ServerSessionId);
        Assert.Equal("fmt", (string?)PayloadValue(payload, "name"));
    }

    [Fact]
    public async Task ResolveToolAsync_does_not_block_on_a_stalled_transport()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionToolWire(
            "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
            HasRenderCall: true, HasRenderResult: false,
            RendererName: null, RenderShell: null,
            ExecutionMode: ToolExecutionMode.Parallel, PromptSnippet: "Use fmt", PromptGuidelines: ["guideline"]);
        // An explicitly-gated reply: the wire round trip stays stalled until the gate is released.
        var replyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new BackendFakeTransport
        {
            ReplyGate = replyGate,
            Responder = type => type == ServerCommandTypes.ResolveTool
                ? ServerResponse.Ok("st", type, wire)
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        // Fire the resolve without awaiting: the transport gates the wire reply on ReplyGate, so
        // the round trip cannot complete until the gated reply is explicitly released below.
        var resolveTask = backend.ResolveToolAsync("fmt", CancellationToken.None);

        // While the reply is still gated the task must be incomplete. A regressed
        // .GetAwaiter().GetResult() would block this thread here until release (never), so reaching
        // this assertion with control back on the test thread proves the caller did not block.
        await Task.Delay(100);
        Assert.False(resolveTask.IsCompleted);

        // Release the stalled reply; the resolve then completes on its own.
        replyGate.TrySetResult();

        var tool = await resolveTask;

        Assert.NotNull(tool);
        Assert.Equal("fmt", tool!.Name);
    }

    [Fact]
    public async Task ResolveTool_NonRenderableWire_YieldsRendererWithNoCapabilities()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionToolWire(
            "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
            HasRenderCall: false, HasRenderResult: false,
            RendererName: null, RenderShell: null,
            ExecutionMode: null, PromptSnippet: null, PromptGuidelines: null);
        var transport = new BackendFakeTransport
        {
            Responder = type => type == ServerCommandTypes.ResolveTool
                ? ServerResponse.Ok("st", type, wire)
                : ServerResponse.Ok("st", type),
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var tool = await backend.ResolveToolAsync("fmt", CancellationToken.None);

        var renderer = Assert.IsAssignableFrom<IAgentToolRenderer>(tool);
        Assert.False(renderer.HasRenderCall);
        Assert.False(renderer.HasRenderResult);
    }

    [Fact]
    public async Task ResolveTool_RenderCallAsync_SendsRenderToolCall_AndMapsLines()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionToolWire(
            "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
            HasRenderCall: true, HasRenderResult: false,
            RendererName: null, RenderShell: null,
            ExecutionMode: null, PromptSnippet: null, PromptGuidelines: null);
        var transport = new BackendFakeTransport
        {
            Responder = type => type switch
            {
                ServerCommandTypes.ResolveTool => ServerResponse.Ok("st", type, wire),
                ServerCommandTypes.RenderToolCall => ServerResponse.Ok("st", type, new { lines = new[] { "call line" } }),
                _ => ServerResponse.Ok("st", type),
            },
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var tool = await backend.ResolveToolAsync("fmt", CancellationToken.None);
        var renderer = Assert.IsAssignableFrom<IAgentToolRenderer>(tool);
        var rendered = await renderer.RenderCallAsync(
            new ToolRenderRequest("tc-1", "fmt", schema.RootElement.Clone(), null, IsPartial: true, IsError: false, Expanded: false, Width: 120),
            CancellationToken.None);

        Assert.NotNull(rendered);
        Assert.Equal(["call line"], rendered.Lines);
        var renderCommand = Assert.Single(transport.Commands, command => command.Envelope.Type == ServerCommandTypes.RenderToolCall);
        Assert.Equal("tc-1", (string?)PayloadValue(renderCommand.Payload, "ToolCallId"));
        Assert.Equal("fmt", (string?)PayloadValue(renderCommand.Payload, "Name"));
        Assert.Equal(true, (bool?)PayloadValue(renderCommand.Payload, "IsCall"));
        Assert.Equal(120, (int?)PayloadValue(renderCommand.Payload, "Width"));
    }

    [Fact]
    public async Task ResolveTool_RenderResultAsync_SendsRenderToolResult_AndMapsLines()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionToolWire(
            "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
            HasRenderCall: true, HasRenderResult: true,
            RendererName: null, RenderShell: null,
            ExecutionMode: null, PromptSnippet: null, PromptGuidelines: null);
        var transport = new BackendFakeTransport
        {
            Responder = type => type switch
            {
                ServerCommandTypes.ResolveTool => ServerResponse.Ok("st", type, wire),
                ServerCommandTypes.RenderToolResult => ServerResponse.Ok("st", type, new { lines = new[] { "result line" } }),
                _ => ServerResponse.Ok("st", type),
            },
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var tool = await backend.ResolveToolAsync("fmt", CancellationToken.None);
        var renderer = Assert.IsAssignableFrom<IAgentToolRenderer>(tool);
        var rendered = await renderer.RenderResultAsync(
            new ToolRenderRequest("tc-1", "fmt", Arguments: null, null, IsPartial: false, IsError: true, Expanded: true, Width: 120),
            CancellationToken.None);

        Assert.NotNull(rendered);
        Assert.Equal(["result line"], rendered.Lines);
        var renderCommand = Assert.Single(transport.Commands, command => command.Envelope.Type == ServerCommandTypes.RenderToolResult);
        Assert.Equal("tc-1", (string?)PayloadValue(renderCommand.Payload, "ToolCallId"));
        Assert.Equal(true, (bool?)PayloadValue(renderCommand.Payload, "IsError"));
        Assert.Equal(false, (bool?)PayloadValue(renderCommand.Payload, "IsCall"));
    }

    [Fact]
    public async Task ResolveTool_RenderCallAsync_ServerFailure_ReturnsNull()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var wire = new ExtensionToolWire(
            "fmt", "Format", "Pretty-prints code", schema.RootElement.Clone(),
            HasRenderCall: true, HasRenderResult: false,
            RendererName: null, RenderShell: null,
            ExecutionMode: null, PromptSnippet: null, PromptGuidelines: null);
        var transport = new BackendFakeTransport
        {
            Responder = type => type switch
            {
                ServerCommandTypes.ResolveTool => ServerResponse.Ok("st", type, wire),
                ServerCommandTypes.RenderToolCall => ServerResponse.Fail("st", type, "not_available", "Tool 'fmt' is not registered."),
                _ => ServerResponse.Ok("st", type),
            },
        };
        var connection = new ClientSessionConnection(transport, NullLogger.Instance);
        await using var backend = new RemoteTuiBackend(connection, NullLogger.Instance) { ServerSessionId = SessionId };

        var tool = await backend.ResolveToolAsync("fmt", CancellationToken.None);
        var renderer = Assert.IsAssignableFrom<IAgentToolRenderer>(tool);
        var rendered = await renderer.RenderCallAsync(
            new ToolRenderRequest("tc-1", "fmt", schema.RootElement.Clone(), null, IsPartial: true, IsError: false, Expanded: false, Width: 120),
            CancellationToken.None);

        Assert.Null(rendered);
    }

    // --- helpers ---

    private static ServerEventEnvelope MessageEnvelope(long sequence, string text)
        => ServerEventEnvelope.FromFlat(SessionId, sequence,
            AgentSessionEvent.FromCore(new AgentEvent.MessageStart(new UserMessage([new TextContent(text)]))));

    private static ServerEventEnvelope CoreEnvelope(long sequence, AgentEvent coreEvent)
        => ServerEventEnvelope.FromFlat(SessionId, sequence, AgentSessionEvent.FromCore(coreEvent));

    private static ServerEventEnvelope OwnEnvelope(long sequence, AgentHarnessOwnEvent ownEvent)
        => ServerEventEnvelope.FromFlat(SessionId, sequence, AgentSessionEvent.FromOwn(ownEvent));

    private static object? PayloadValue(object? payload, string property)
        => payload?.GetType().GetProperty(property)?.GetValue(payload);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }

    private sealed class BackendFakeTransport : IClientTransport
    {
        public Channel<ServerEventEnvelope> Events { get; } = Channel.CreateUnbounded<ServerEventEnvelope>();
        public Channel<ServerResponse> Late { get; } = Channel.CreateUnbounded<ServerResponse>();
        public List<(ServerCommandEnvelope Envelope, object? Payload)> Commands { get; } = [];
        public Func<string, ServerResponse>? Responder { get; set; }
        public TimeSpan? ResponseDelay { get; set; }
        public TaskCompletionSource? ReplyGate { get; set; }

        ChannelReader<ServerEventEnvelope> IClientTransport.Events => Events.Reader;
        ChannelReader<ServerResponse> IClientTransport.LateResponses => Late.Reader;

        public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct) => Task.CompletedTask;

        public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct, TimeSpan? timeoutOverride = null)
            => SendCommandAsync(envelope, payload: null, ct, timeoutOverride);

        public async Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct, TimeSpan? timeoutOverride = null)
        {
            if (ResponseDelay is { } delay) await Task.Delay(delay, ct);
            if (ReplyGate is { } gate) await gate.Task;
            Commands.Add((envelope, payload));
            return Responder?.Invoke(envelope.Type) ?? ServerResponse.Ok(envelope.Id, envelope.Type);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
