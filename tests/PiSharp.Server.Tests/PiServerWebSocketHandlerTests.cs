using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
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
    public async Task OversizedMessage_ClosesWithMessageTooBig_AndDoesNotDispatch()
    {
        const int maxBytes = 1024;
        var dispatched = 0;
        var delegates = new PiServerCommandDelegates(RunCommandAsync: (_, _, _, _) =>
        {
            Interlocked.Increment(ref dispatched);
            return Task.FromResult(new ServerCommandResult(true, "ok"));
        });
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry, delegates: delegates, options: new PiServerHostOptions { ApiKey = "secret", MaxMessageBytes = maxBytes });
        var stub = new StubWebSocket();

        var runTask = handler.RunSocketAsync(stub, CancellationToken.None);
        stub.EnqueueText(JsonSerializer.Serialize(new
        {
            id = "big", type = ServerCommandTypes.RunCommand, serverSessionId = "nope", text = new string('x', maxBytes + 1)
        }, ServerJsonSerializer.Options));

        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Volatile.Read(ref dispatched));
        Assert.Empty(stub.OutgoingFrames);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, stub.ServerCloseStatus);
    }

    [Fact]
    public async Task ConcurrentMessages_BoundDispatchToMaxConcurrentCommands()
    {
        const int maxConcurrent = 2;
        const int count = 50;
        var active = 0;
        var peak = 0;
        var completed = 0;
        var delegates = new PiServerCommandDelegates(CompleteCommandAsync: async (_, _) =>
        {
            var now = Interlocked.Increment(ref active);
            try
            {
                Max(ref peak, now);
                await Task.Delay(30);
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
            finally
            {
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref completed);
            }
        });
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry, delegates: delegates, options: new PiServerHostOptions { ApiKey = "secret", MaxConcurrentCommands = maxConcurrent });

        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        var stub = new StubWebSocket();
        var runTask = handler.RunSocketAsync(stub, CancellationToken.None);

        for (var i = 0; i < count; i++)
        {
            stub.EnqueueText(JsonSerializer.Serialize(new
            {
                id = "c" + i, type = ServerCommandTypes.CompleteCommand, serverSessionId = created.ServerSessionId, text = "t" + i
            }, ServerJsonSerializer.Options));
        }

        await WaitUntilAsync(() => Volatile.Read(ref completed) == count, TimeSpan.FromSeconds(30));

        Assert.Equal(maxConcurrent, Volatile.Read(ref peak));
        Assert.Equal(count, Volatile.Read(ref completed));

        stub.EnqueueClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Shutdown_WithoutConfirmation_FailsWithoutStopping()
    {
        var shutdownCalled = false;
        var delegates = new PiServerCommandDelegates(OnShutdown: _ =>
        {
            Volatile.Write(ref shutdownCalled, true);
            return Task.CompletedTask;
        });
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry, delegates: delegates);
        var stub = new StubWebSocket();

        var runTask = handler.RunSocketAsync(stub, CancellationToken.None);
        stub.EnqueueText(JsonSerializer.Serialize(new { id = "s", type = ServerCommandTypes.Shutdown }, ServerJsonSerializer.Options));

        var frames = await stub.WaitForFramesAsync(1, TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(frames[0]);
        var root = doc.RootElement;
        Assert.Equal("shutdown", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("confirmation_required", root.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Volatile.Read(ref shutdownCalled));

        stub.EnqueueClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Shutdown_WithConfirmation_ReturnsOkAndStopsHost()
    {
        var stopObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegates = new PiServerCommandDelegates(OnShutdown: _ =>
        {
            stopObserved.TrySetResult();
            return Task.CompletedTask;
        });
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry, delegates: delegates);
        var stub = new StubWebSocket();

        var runTask = handler.RunSocketAsync(stub, CancellationToken.None);
        stub.EnqueueText(JsonSerializer.Serialize(new { id = "s", type = ServerCommandTypes.Shutdown, confirm = true }, ServerJsonSerializer.Options));

        var frames = await stub.WaitForFramesAsync(1, TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(frames[0]);
        var root = doc.RootElement;
        Assert.Equal("shutdown", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());

        await stopObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stub.EnqueueClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConcurrentAttachCommands_StartExactlyOneEventPump()
    {
        const int attaches = 4;
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry, options: new PiServerHostOptions { ApiKey = "secret", MaxConcurrentCommands = attaches });
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));

        var stub = new StubWebSocket();
        var runTask = handler.RunSocketAsync(stub, CancellationToken.None);

        for (var i = 0; i < attaches; i++)
        {
            stub.EnqueueText(JsonSerializer.Serialize(new
            {
                id = "a" + i, type = ServerCommandTypes.Attach, serverSessionId = created.ServerSessionId, sinceSequence = 0L
            }, ServerJsonSerializer.Options));
        }

        await stub.WaitForFramesAsync(attaches, TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => live.AttachedClients >= 1, TimeSpan.FromSeconds(5));
        await Task.Delay(200); // give any spurious extra pump time to surface
        Assert.Equal(1, live.AttachedClients);

        stub.EnqueueClose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, live.AttachedClients);
    }

    private static void Max(ref int location, int value)
    {
        int current;
        while ((current = Volatile.Read(ref location)) < value)
        {
            if (Interlocked.CompareExchange(ref location, value, current) == current) return;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }

    private static PiServerWebSocketHandler CreateHandler(ServerSessionRegistry registry, IServerUiBridge? uiBridge = null, PiServerCommandDelegates? delegates = null, PiServerHostOptions? options = null)
        => new(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance, uiBridge, delegates, options: options);

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])), initial);
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

    /// <summary>
    /// In-memory <see cref="WebSocket"/> twin: inbound text frames are pushed via <see cref="EnqueueText"/>,
    /// server responses are recorded as UTF-8 strings, and every close the server sends is captured
    /// so tests can assert close codes (e.g. <see cref="WebSocketCloseStatus.MessageTooBig"/>).
    /// </summary>
    private sealed class StubWebSocket : WebSocket
    {
        private readonly Channel<IncomingMessage> _incoming = Channel.CreateUnbounded<IncomingMessage>();
        private readonly ConcurrentQueue<string> _outgoing = new();
        private readonly Channel<string> _outgoingSignals = Channel.CreateUnbounded<string>();
        private int _closedByServer;
        private WebSocketCloseStatus _serverCloseStatus;

        private sealed record IncomingMessage(WebSocketMessageType Type, string? Text);

        public IReadOnlyCollection<string> OutgoingFrames => _outgoing;
        public WebSocketCloseStatus? ServerCloseStatus
            => Volatile.Read(ref _closedByServer) == 1 ? _serverCloseStatus : null;

        public void EnqueueText(string json) => _incoming.Writer.TryWrite(new IncomingMessage(WebSocketMessageType.Text, json));
        public void EnqueueClose() => _incoming.Writer.TryWrite(new IncomingMessage(WebSocketMessageType.Close, null));

        public async Task<string[]> WaitForFramesAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var frames = new List<string>(count);
            for (var i = 0; i < count; i++) frames.Add(await _outgoingSignals.Reader.ReadAsync(cts.Token));
            return frames.ToArray();
        }

        public override WebSocketState State => Volatile.Read(ref _closedByServer) == 1 ? WebSocketState.CloseSent : WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _closedByServer, 1);
            _serverCloseStatus = closeStatus;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => ReceiveCoreAsync(buffer, cancellationToken);

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveCoreAsync(new ArraySegment<byte>(buffer.ToArray()), cancellationToken);
            return new ValueWebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }

        private async Task<WebSocketReceiveResult> ReceiveCoreAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var message = await _incoming.Reader.ReadAsync(cancellationToken);
            if (message.Type == WebSocketMessageType.Close) return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            var bytes = Encoding.UTF8.GetBytes(message.Text ?? string.Empty);
            bytes.AsSpan().CopyTo(buffer);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            _outgoing.Enqueue(text);
            _outgoingSignals.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => new(SendAsync(new ArraySegment<byte>(buffer.ToArray()), messageType, endOfMessage, cancellationToken));
    }
}
