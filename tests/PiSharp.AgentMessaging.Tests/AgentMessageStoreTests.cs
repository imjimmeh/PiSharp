using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class AgentMessageStoreTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pisharp-agentmessaging-store", Guid.NewGuid().ToString("N"));
    private AgentMessageStore _store = null!;

    public Task InitializeAsync()
    {
        _store = new AgentMessageStore(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    private static AgentMessage Queued(string id, string body = "hello", DateTimeOffset? timestamp = null)
        => new(
            MessageId: id,
            FromAgentId: "child-a",
            ToAgentIds: ["child-b"],
            Body: body,
            Delivery: AgentMessageDelivery.Steer,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            Status: AgentMessageStatus.Queued);

    [Fact]
    public async Task AppendAndLoad_RoundTripsMessages()
    {
        await _store.AppendAsync(Queued("m1", "first"));
        await _store.AppendAsync(Queued("m2", "second"));

        var loaded = await _store.LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(["m1", "m2"], loaded.Select(m => m.MessageId).ToArray());
        Assert.Equal("first", loaded[0].Body);
        Assert.Equal(AgentMessageStatus.Queued, loaded[0].Status);
    }

    [Fact]
    public async Task Load_DeduplicatesRepeatedAppends()
    {
        await _store.AppendAsync(Queued("m1"));
        await _store.AppendAsync(Queued("m1"));

        var loaded = await _store.LoadAsync();

        Assert.Single(loaded);
    }

    [Fact]
    public async Task MarkFailed_RemovesMessageViaTombstone()
    {
        await _store.AppendAsync(Queued("m1"));
        await _store.AppendAsync(Queued("m2"));

        await _store.MarkFailedAsync("m1");

        var loaded = await _store.LoadAsync();
        Assert.Single(loaded);
        Assert.Equal("m2", loaded[0].MessageId);
    }

    [Fact]
    public async Task MarkFailed_UnknownMessage_IsNoOp()
    {
        await _store.AppendAsync(Queued("m1"));

        await _store.MarkFailedAsync("ghost");

        Assert.Single(await _store.LoadAsync());
    }

    [Fact]
    public async Task CleanupExpired_FailsOnlyStaleQueuedMessages()
    {
        await _store.AppendAsync(Queued("stale", timestamp: DateTimeOffset.UtcNow.AddHours(-48)));
        await _store.AppendAsync(Queued("fresh", timestamp: DateTimeOffset.UtcNow));

        var failed = await _store.CleanupExpiredAsync(ttlHours: 24);

        Assert.Equal(1, failed);
        var remaining = await _store.LoadAsync();
        var message = Assert.Single(remaining);
        Assert.Equal("fresh", message.MessageId);
    }

    [Fact]
    public async Task CleanupExpired_ZeroTtl_IsNoOp()
    {
        await _store.AppendAsync(Queued("m1", timestamp: DateTimeOffset.UtcNow.AddYears(-1)));

        var failed = await _store.CleanupExpiredAsync(ttlHours: 0);

        Assert.Equal(0, failed);
        Assert.Single(await _store.LoadAsync());
    }

    [Fact]
    public async Task CleanupExpired_IgnoresDeliveredMessages()
    {
        var delivered = Queued("m1") with { Status = AgentMessageStatus.Delivered };
        await _store.AppendAsync(delivered with { Timestamp = DateTimeOffset.UtcNow.AddHours(-48) });

        var failed = await _store.CleanupExpiredAsync(ttlHours: 24);

        Assert.Equal(0, failed);
        Assert.Single(await _store.LoadAsync());
    }

    [Fact]
    public async Task Load_EmptyFile_ReturnsEmpty()
    {
        Assert.Empty(await _store.LoadAsync());
    }

    [Fact]
    public async Task Load_AfterReopen_ReplaysPersistedMessages()
    {
        await _store.AppendAsync(Queued("m1"));

        // Simulate a daemon restart: a fresh store instance over the same dir.
        var reopened = new AgentMessageStore(_dir);
        var loaded = await reopened.LoadAsync();

        var message = Assert.Single(loaded);
        Assert.Equal("m1", message.MessageId);
    }

    [Fact]
    public async Task ConcurrentAppends_AllSurvive()
    {
        const int count = 25;
        var tasks = Enumerable.Range(0, count)
            .Select(i => _store.AppendAsync(Queued($"m{i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(count, (await _store.LoadAsync()).Count);
    }
}
