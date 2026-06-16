using Xunit;

namespace PiSharp.Coordination.Tests;

public sealed class CoordinationStateTests
{
    [Fact]
    public void ApplyReplaysRecordsIntoExpectedIndexes()
    {
        var state = new CoordinationState();

        state.Apply(new AgentRegisteredRecord("agent-1", 123, "s1", null, "/repo", DateTimeOffset.UnixEpoch));
        state.Apply(new AgentHeartbeatRecord("agent-1", DateTimeOffset.UnixEpoch.AddHours(1)));
        state.Apply(new MessageSentRecord("msg-1", "agent-1", "all", "hello", DateTimeOffset.UnixEpoch));
        state.Apply(new FileReadRecord("agent-1", "/read.txt", DateTimeOffset.UnixEpoch));
        state.Apply(new FileWriteRecord("agent-1", "/write.txt", DateTimeOffset.UnixEpoch));

        Assert.Contains(state.Agents.Keys, k => k == "agent-1");
        Assert.True(state.AgentLastSeen.TryGetValue("agent-1", out var lastSeen));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), lastSeen);
        Assert.Contains(state.Messages, m => m.MessageId == "msg-1");
        Assert.Contains(state.RecentReads, r => r.Path == "/read.txt");
        Assert.Contains(state.RecentWrites, w => w.Path == "/write.txt");
    }

    [Fact]
    public void ApplyingAgentUnregisteredRecordRemovesAgentFromState()
    {
        var state = new CoordinationState();

        state.Apply(new AgentRegisteredRecord("agent-1", 123, "s1", null, "/repo", DateTimeOffset.UnixEpoch));
        Assert.Contains(state.Agents.Keys, k => k == "agent-1");

        state.Apply(new AgentUnregisteredRecord("agent-1", DateTimeOffset.UnixEpoch.AddHours(1)));
        Assert.DoesNotContain(state.Agents.Keys, k => k == "agent-1");
    }
}
