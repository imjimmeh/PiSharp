using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;
using PiSharp.Tui.Interactive;
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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };
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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

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
        var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection) { ServerSessionId = SessionId };

        var intent = new ServerUiIntent("r1", "notify", "Title", "message", null, null);
        transport.Events.Writer.TryWrite(ServerEventEnvelope.FromFlat(
            SessionId, 1, AgentSessionEvent.FromServer("ui_request", intent)));

        await WaitUntilAsync(() => transport.Commands.Any(command => command.Envelope.Type == ServerCommandTypes.UiResponse));
        var (_, payload) = Assert.Single(transport.Commands, command => command.Envelope.Type == ServerCommandTypes.UiResponse);
        Assert.Equal("r1", (string?)PayloadValue(payload, "requestId"));
        Assert.Equal(true, (bool?)PayloadValue(payload, "cancelled"));
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
        public List<(ServerCommandEnvelope Envelope, object? Payload)> Commands { get; } = [];
        public Func<string, ServerResponse>? Responder { get; set; }

        ChannelReader<ServerEventEnvelope> IClientTransport.Events => Events.Reader;

        public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct) => Task.CompletedTask;

        public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct, TimeSpan? timeoutOverride = null)
            => SendCommandAsync(envelope, payload: null, ct, timeoutOverride);

        public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct, TimeSpan? timeoutOverride = null)
        {
            Commands.Add((envelope, payload));
            return Task.FromResult(Responder?.Invoke(envelope.Type) ?? ServerResponse.Ok(envelope.Id, envelope.Type));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
