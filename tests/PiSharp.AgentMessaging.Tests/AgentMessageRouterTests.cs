using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class AgentMessageRouterTests : IAsyncLifetime
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pisharp-agentmessaging-tests", Guid.NewGuid().ToString("N"));
    private AgentRosterService _roster = null!;
    private AgentMessageStore _store = null!;
    private List<AgentMessage> _delivered = [];
    private AgentMessageRouter _router = null!;

    public Task InitializeAsync()
    {
        _roster = new AgentRosterService();
        _roster.Register(TestAgents.Agent("root"));
        _roster.Register(TestAgents.Agent("child-a", parent: "root"));
        _roster.Register(TestAgents.Agent("child-b", parent: "root"));
        _store = new AgentMessageStore(_storeDir);
        _delivered = [];
        _router = new AgentMessageRouter(
            _roster,
            _store,
            new AgentMessagingOptions(),
            (message, _) =>
            {
                _delivered.Add(message);
                return Task.CompletedTask;
            });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _router.DisposeAsync();
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    [Fact]
    public async Task Send_ToRunningRecipient_DeliversLive()
    {
        var result = await _router.SendAsync("child-a", ["child-b"], "hello sibling", AgentMessageDelivery.Steer);

        Assert.False(result.IsError);
        var recipient = Assert.Single(result.Recipients);
        Assert.Equal("child-b", recipient.AgentId);
        Assert.Equal(AgentMessageStatus.Delivered, recipient.Status);

        var delivered = Assert.Single(_delivered);
        Assert.Equal("child-a", delivered.FromAgentId);
        Assert.Equal(["child-b"], delivered.ToAgentIds);
        Assert.Equal("hello sibling", delivered.Body);

        var inbox = _router.GetInbox("child-b");
        var inboxMessage = Assert.Single(inbox);
        Assert.Equal(delivered.MessageId, inboxMessage.MessageId);
    }

    [Fact]
    public async Task Send_ToPassivatedRecipient_QueuesAndPersists()
    {
        _roster.UpdateStatus("child-b", AgentStatus.Passivated);

        var result = await _router.SendAsync("child-a", ["child-b"], "hold for me", AgentMessageDelivery.FollowUp);

        Assert.False(result.IsError);
        Assert.Equal(AgentMessageStatus.Queued, Assert.Single(result.Recipients).Status);
        Assert.Empty(_delivered);

        var stored = await _store.LoadAsync();
        var queued = Assert.Single(stored);
        Assert.Equal(AgentMessageStatus.Queued, queued.Status);
        Assert.Empty(_router.GetInbox("child-b"));
    }

    [Fact]
    public async Task Send_ToGoneRecipient_Fails()
    {
        _roster.UpdateStatus("child-b", AgentStatus.Gone);

        var result = await _router.SendAsync("child-a", ["child-b"], "too late", AgentMessageDelivery.Steer);

        Assert.False(result.IsError);
        Assert.Equal(AgentMessageStatus.Failed, Assert.Single(result.Recipients).Status);
        Assert.Empty(_delivered);
        Assert.Empty(await _store.LoadAsync());
    }

    [Fact]
    public async Task Send_UnknownTarget_ReturnsTypedError()
    {
        var result = await _router.SendAsync("child-a", ["nobody"], "hi", AgentMessageDelivery.Steer);

        Assert.True(result.IsError);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
        Assert.Empty(result.Recipients);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Send_SelfTarget_ReturnsTypedError()
    {
        var result = await _router.SendAsync("child-a", ["child-a"], "hi", AgentMessageDelivery.Steer);

        Assert.True(result.IsError);
        Assert.Equal(AgentMessagingErrorCodes.TargetInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task Send_All_FansOutWithPerRecipientStatus()
    {
        _roster.UpdateStatus("child-b", AgentStatus.Passivated);

        var result = await _router.SendAsync("child-a", ["all"], "broadcast", AgentMessageDelivery.Steer);

        Assert.False(result.IsError);
        var statuses = result.Recipients.ToDictionary(r => r.AgentId, r => r.Status);
        Assert.Equal(2, statuses.Count); // root + child-b
        Assert.Equal(AgentMessageStatus.Delivered, statuses["root"]);
        Assert.Equal(AgentMessageStatus.Queued, statuses["child-b"]);
        Assert.Single(_delivered);
    }

    [Fact]
    public async Task Send_Disabled_ReturnsDisabledError()
    {
        _router = new AgentMessageRouter(_roster, _store, new AgentMessagingOptions { Enabled = false }, (m, _) =>
        {
            _delivered.Add(m);
            return Task.CompletedTask;
        });

        var result = await _router.SendAsync("child-a", ["child-b"], "hi");

        Assert.True(result.IsError);
        Assert.Equal(AgentMessagingErrorCodes.Disabled, result.ErrorCode);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Send_BodyTooLong_ReturnsLengthError()
    {
        var router = new AgentMessageRouter(_roster, _store, new AgentMessagingOptions { MaxInboxMessageLength = 16 }, (m, _) =>
        {
            _delivered.Add(m);
            return Task.CompletedTask;
        });

        var result = await router.SendAsync("child-a", ["child-b"], new string('x', 17));

        Assert.True(result.IsError);
        Assert.Equal(AgentMessagingErrorCodes.BodyTooLong, result.ErrorCode);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Send_DeliveryCallbackFailure_QueuesInsteadOfDropping()
    {
        _router = new AgentMessageRouter(_roster, _store, new AgentMessagingOptions(), (_, _) =>
            throw new InvalidOperationException("harness down"));

        var result = await _router.SendAsync("child-a", ["child-b"], "deliver me");

        Assert.False(result.IsError);
        Assert.Equal(AgentMessageStatus.Queued, Assert.Single(result.Recipients).Status);
        Assert.Single(await _store.LoadAsync());
    }

    [Fact]
    public async Task Replay_DeliversQueuedMessagesToNowRunningRecipients()
    {
        // child-b passivated when the message is sent → queued + persisted.
        _roster.UpdateStatus("child-b", AgentStatus.Passivated);
        var sent = await _router.SendAsync("child-a", ["child-b"], "resume me", AgentMessageDelivery.Steer);
        var messageId = sent.MessageId;
        Assert.Empty(_delivered);

        // child-b resumes → replay delivers and drops the outbox entry.
        _roster.UpdateStatus("child-b", AgentStatus.Running);
        await _router.ReplayAsync();

        var delivered = Assert.Single(_delivered);
        Assert.Equal(messageId, delivered.MessageId);
        Assert.Single(_router.GetInbox("child-b"));
        Assert.Empty(await _store.LoadAsync());
    }

    [Fact]
    public async Task Replay_WithStillPassivatedRecipient_KeepsOutboxEntry()
    {
        _roster.UpdateStatus("child-b", AgentStatus.Passivated);
        await _router.SendAsync("child-a", ["child-b"], "still waiting", AgentMessageDelivery.Steer);

        await _router.ReplayAsync();

        Assert.Empty(_delivered);
        Assert.Single(await _store.LoadAsync());
    }

    [Fact]
    public async Task CleanupExpired_FailsStaleQueuedMessages()
    {
        // A message that was queued well beyond the TTL.
        var stale = new AgentMessage(
            MessageId: "stale-1",
            FromAgentId: "child-a",
            ToAgentIds: ["child-b"],
            Body: "ancient",
            Delivery: AgentMessageDelivery.Steer,
            Timestamp: DateTimeOffset.UtcNow.AddHours(-48),
            Status: AgentMessageStatus.Queued);
        await _store.AppendAsync(stale);

        var router = new AgentMessageRouter(_roster, _store, new AgentMessagingOptions { QueuedMessageTtlHours = 24 }, (m, _) =>
        {
            _delivered.Add(m);
            return Task.CompletedTask;
        });

        var failed = await router.CleanupExpiredAsync();

        Assert.Equal(1, failed);
        Assert.Empty(await _store.LoadAsync());
    }

    [Fact]
    public async Task GetInbox_SinceWindow_ReturnsOnlyNewerMessages()
    {
        var first = await _router.SendAsync("child-a", ["child-b"], "first", AgentMessageDelivery.Steer);
        await _router.SendAsync("child-a", ["child-b"], "second", AgentMessageDelivery.Steer);

        var newer = _router.GetInbox("child-b", sinceMessageId: first.MessageId);

        var message = Assert.Single(newer);
        Assert.Equal("second", message.Body);
    }

    [Fact]
    public async Task GetInbox_Limit_CapsResults()
    {
        for (var i = 0; i < 5; i++)
            await _router.SendAsync("child-a", ["child-b"], $"msg-{i}", AgentMessageDelivery.Steer);

        var inbox = _router.GetInbox("child-b", limit: 2);

        Assert.Equal(2, inbox.Count);
        Assert.Equal("msg-4", inbox[0].Body); // newest first
    }

    [Fact]
    public async Task Steer_IsASteerDeliverySend()
    {
        var result = await _router.SteerAsync("child-a", "child-b", "do the thing");

        Assert.False(result.IsError);
        var delivered = Assert.Single(_delivered);
        Assert.Equal(AgentMessageDelivery.Steer, delivered.Delivery);
        Assert.Equal(["child-b"], delivered.ToAgentIds);
    }
}
