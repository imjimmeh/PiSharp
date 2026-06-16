using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class CoordinationJsonlStoreTests
{
    [Fact]
    public async Task StoreReplaysAgentAndMessageRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        await store.AppendAsync(new AgentRegisteredRecord("agent-1", 123, "session-1", null, "/repo", DateTimeOffset.UnixEpoch));
        await store.AppendAsync(new MessageSentRecord("msg-1", "agent-1", "all", "hello", DateTimeOffset.UnixEpoch));

        var records = await store.ReadAllAsync();

        Assert.Contains(records, record => record is AgentRegisteredRecord { AgentId: "agent-1" });
        Assert.Contains(records, record => record is MessageSentRecord { MessageId: "msg-1", Body: "hello" });
    }

    [Fact]
    public async Task ReadAllReturnsEmptyWhenNoFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var records = await store.ReadAllAsync();

        Assert.Empty(records);
    }

    [Fact]
    public async Task ReadAllThrowsForUnknownRecordType()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"unknown_type\",\"timestamp\":\"2024-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("unknown_type", ex.Message);
    }

    [Fact]
    public async Task ConcurrentAppendsAllRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var tasks = Enumerable.Range(0, 100).Select(i =>
            store.AppendAsync(new AgentHeartbeatRecord($"agent-{i}", DateTimeOffset.UnixEpoch.AddSeconds(i))));

        await Task.WhenAll(tasks);

        var records = await store.ReadAllAsync();
        Assert.Equal(100, records.Count);
        for (var i = 0; i < 100; i++)
        {
            var agentId = $"agent-{i}";
            Assert.Contains(records, r => r is AgentHeartbeatRecord h && h.AgentId == agentId);
        }
    }

    [Fact]
    public async Task MultipleStoreInstancesShareWriteLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var store1 = new CoordinationJsonlStore(directory);
        var store2 = new CoordinationJsonlStore(directory);
        var store3 = new CoordinationJsonlStore(directory);

        var tasks = new[]
        {
            Task.Run(() => store1.AppendAsync(new AgentHeartbeatRecord("h1", DateTimeOffset.UnixEpoch))),
            Task.Run(() => store2.AppendAsync(new AgentHeartbeatRecord("h2", DateTimeOffset.UnixEpoch.AddSeconds(1)))),
            Task.Run(() => store3.AppendAsync(new AgentHeartbeatRecord("h3", DateTimeOffset.UnixEpoch.AddSeconds(2))))
        };

        await Task.WhenAll(tasks);

        var records = await store1.ReadAllAsync();
        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r is AgentHeartbeatRecord h && h.AgentId == "h1");
        Assert.Contains(records, r => r is AgentHeartbeatRecord h && h.AgentId == "h2");
        Assert.Contains(records, r => r is AgentHeartbeatRecord h && h.AgentId == "h3");
    }

    [Fact]
    public async Task ReadAllThrowsForBlankTypeDiscriminator()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"\",\"timestamp\":\"2024-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("blank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMissingTypeDiscriminator()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"timestamp\":\"2024-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedAgentRegisteredMissingAgentId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"agent_registered\",\"processId\":1,\"cwd\":\"/\",\"timestamp\":\"2024-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("AgentId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedMessageSentMissingBody()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"message_sent\",\"messageId\":\"m1\",\"fromAgentId\":\"a1\",\"to\":\"all\",\"timestamp\":\"2024-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Body", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedFileReadMissingPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"file_read\",\"agentId\":\"a1\",\"timestamp\":\"2024-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentlyCasedDirectoryPathsShareLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var upperDirectory = directory.ToUpperInvariant();

        var store1 = new CoordinationJsonlStore(directory);
        var store2 = new CoordinationJsonlStore(upperDirectory);

        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() => store1.AppendAsync(
                new AgentHeartbeatRecord($"h1-{idx}", DateTimeOffset.UnixEpoch.AddSeconds(idx)))));
            tasks.Add(Task.Run(() => store2.AppendAsync(
                new AgentHeartbeatRecord($"h2-{idx}", DateTimeOffset.UnixEpoch.AddSeconds(idx)))));
        }

        await Task.WhenAll(tasks);

        var records = await store1.ReadAllAsync();
        Assert.Equal(100, records.Count);
        for (var i = 0; i < 50; i++)
        {
            var idx = i;
            Assert.Contains(records, r => r is AgentHeartbeatRecord h && h.AgentId == $"h1-{idx}");
            Assert.Contains(records, r => r is AgentHeartbeatRecord h && h.AgentId == $"h2-{idx}");
        }
    }

    [Fact]
    public async Task AppendAsyncRejectsInvalidRecordAndWritesNoLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var invalid = new AgentRegisteredRecord("", 1, null, null, "/", DateTimeOffset.UnixEpoch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(invalid));
        Assert.Contains("AgentId", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(directory, "events.jsonl")));
    }

    [Fact]
    public async Task ReadAllRejectsRecordMissingTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"agent_heartbeat\",\"agentId\":\"a1\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendAsyncRejectsDefaultTimestampAndWritesNoLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var invalid = new AgentHeartbeatRecord("a1", default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(invalid));
        Assert.Contains("Timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(directory, "events.jsonl")));
    }

    [Fact]
    public async Task StoreReplaysPreflightWarningRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var warning = new PreflightWarningRecord(
            "agent-a", "README.md", "agent-b",
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            DateTimeOffset.UnixEpoch.AddSeconds(3));

        await store.AppendAsync(warning);

        var records = await store.ReadAllAsync();

        Assert.Contains(records, record =>
            record is PreflightWarningRecord r
            && r.AgentId == "agent-a"
            && r.Path == "README.md"
            && r.ConflictingAgentId == "agent-b"
            && r.ConflictingTimestamp == DateTimeOffset.UnixEpoch.AddSeconds(2)
            && r.WarningTimestamp == DateTimeOffset.UnixEpoch.AddSeconds(3));
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedPreflightWarningMissingAgentId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"preflight_warning\",\"path\":\"README.md\",\"conflictingAgentId\":\"agent-b\",\"conflictingTimestamp\":\"1970-01-01T00:00:02+00:00\",\"timestamp\":\"1970-01-01T00:00:03+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("AgentId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedPreflightWarningMissingPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"preflight_warning\",\"agentId\":\"agent-a\",\"conflictingAgentId\":\"agent-b\",\"conflictingTimestamp\":\"1970-01-01T00:00:02+00:00\",\"timestamp\":\"1970-01-01T00:00:03+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedPreflightWarningMissingConflictingAgentId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"preflight_warning\",\"agentId\":\"agent-a\",\"path\":\"README.md\",\"conflictingTimestamp\":\"1970-01-01T00:00:02+00:00\",\"timestamp\":\"1970-01-01T00:00:03+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("ConflictingAgentId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedPreflightWarningMissingWarningTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"preflight_warning\",\"agentId\":\"agent-a\",\"path\":\"README.md\",\"conflictingAgentId\":\"agent-b\",\"conflictingTimestamp\":\"1970-01-01T00:00:02+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedPreflightWarningMissingConflictingTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"preflight_warning\",\"agentId\":\"agent-a\",\"path\":\"README.md\",\"conflictingAgentId\":\"agent-b\",\"timestamp\":\"1970-01-01T00:00:03+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("ConflictingTimestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendAsyncRejectsPreflightWarningWithDefaultWarningTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var invalid = new PreflightWarningRecord(
            "agent-a", "README.md", "agent-b",
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(invalid));
        Assert.Contains("Timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreToleratesMultipleAppends()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        await store.AppendAsync(new AgentRegisteredRecord("a1", 1, null, null, "/", DateTimeOffset.UnixEpoch));
        await store.AppendAsync(new AgentHeartbeatRecord("a1", DateTimeOffset.UnixEpoch));
        await store.AppendAsync(new AgentRegisteredRecord("a2", 2, null, null, "/", DateTimeOffset.UnixEpoch));

        var records = await store.ReadAllAsync();

        Assert.Equal(3, records.Count);
        Assert.Contains(records, record => record is AgentRegisteredRecord { AgentId: "a1" });
        Assert.Contains(records, record => record is AgentHeartbeatRecord { AgentId: "a1" });
        Assert.Contains(records, record => record is AgentRegisteredRecord { AgentId: "a2" });
    }

    [Fact]
    public async Task StoreReplaysSubagentObservedRecordWithAllFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var record = new SubagentObservedRecord(
            "sub-full", "Explore", "inspect files", "completed",
            "subagents:completed", 1500.5, 12, 500, 1200,
            "parent-session", "/repo", DateTimeOffset.UnixEpoch);

        await store.AppendAsync(record);
        var records = await store.ReadAllAsync();

        var replayed = Assert.Single(records.OfType<SubagentObservedRecord>());
        Assert.Equal("sub-full", replayed.SubagentId);
        Assert.Equal("Explore", replayed.SubagentType);
        Assert.Equal("inspect files", replayed.Description);
        Assert.Equal("completed", replayed.Status);
        Assert.Equal("subagents:completed", replayed.EventName);
        Assert.Equal(1500.5, replayed.DurationMs);
        Assert.Equal(12, replayed.ToolUses);
        Assert.Equal(500, replayed.InputTokens);
        Assert.Equal(1200, replayed.OutputTokens);
        Assert.Equal("parent-session", replayed.ParentSessionId);
        Assert.Equal("/repo", replayed.Cwd);
        Assert.Equal(DateTimeOffset.UnixEpoch, replayed.Timestamp);
    }

    [Fact]
    public async Task StoreReplaysSubagentObservedRecordWithMinimalFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var record = new SubagentObservedRecord(
            "sub-min", null, null, null,
            "subagents:created", null, null, null, null,
            null, "/tmp", DateTimeOffset.UnixEpoch);

        await store.AppendAsync(record);
        var records = await store.ReadAllAsync();

        var replayed = Assert.Single(records.OfType<SubagentObservedRecord>());
        Assert.Equal("sub-min", replayed.SubagentId);
        Assert.Null(replayed.SubagentType);
        Assert.Null(replayed.Description);
        Assert.Null(replayed.Status);
        Assert.Null(replayed.DurationMs);
        Assert.Null(replayed.ToolUses);
        Assert.Null(replayed.InputTokens);
        Assert.Null(replayed.OutputTokens);
        Assert.Null(replayed.ParentSessionId);
        Assert.Equal("/tmp", replayed.Cwd);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedSubagentObservedMissingSubagentId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"subagent_observed\",\"eventName\":\"subagents:started\",\"cwd\":\"/repo\",\"timestamp\":\"1970-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("SubagentId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedSubagentObservedMissingEventName()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"subagent_observed\",\"subagentId\":\"sub-1\",\"cwd\":\"/repo\",\"timestamp\":\"1970-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("EventName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedSubagentObservedMissingCwd()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"subagent_observed\",\"subagentId\":\"sub-1\",\"eventName\":\"subagents:started\",\"timestamp\":\"1970-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Cwd", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedSubagentObservedMissingTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"subagent_observed\",\"subagentId\":\"sub-1\",\"eventName\":\"subagents:started\",\"cwd\":\"/repo\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendAsyncRejectsSubagentObservedWithDefaultTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        var invalid = new SubagentObservedRecord(
            "sub-1", null, null, null,
            "subagents:started", null, null, null, null,
            null, "/repo", default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(invalid));
        Assert.Contains("Timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForMalformedSubagentObservedBlankSubagentId()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"subagent_observed\",\"subagentId\":\"\",\"eventName\":\"subagents:started\",\"cwd\":\"/repo\",\"timestamp\":\"1970-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("SubagentId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAllThrowsForSubagentObservedWithUnknownEventName()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new CoordinationJsonlStore(directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"),
            "{\"type\":\"subagent_observed\",\"subagentId\":\"sub-1\",\"eventName\":\"subagents:unknown\",\"cwd\":\"/repo\",\"timestamp\":\"1970-01-01T00:00:00+00:00\"}\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAllAsync());
        Assert.Contains("Unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subagents:unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
