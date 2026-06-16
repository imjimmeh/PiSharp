using System.Collections.Concurrent;

namespace PiSharp.Coordination;

public sealed class CoordinationState
{
    public ConcurrentDictionary<string, AgentRegisteredRecord> Agents { get; } = new();
    public ConcurrentDictionary<string, DateTimeOffset> AgentLastSeen { get; } = new();
    public ConcurrentBag<MessageSentRecord> Messages { get; } = new();
    public ConcurrentBag<FileReadRecord> RecentReads { get; } = new();
    public ConcurrentBag<FileWriteRecord> RecentWrites { get; } = new();
    public ConcurrentBag<PreflightWarningRecord> RecentWarnings { get; } = new();
    public ConcurrentBag<SubagentObservedRecord> SubagentEvents { get; } = new();

    public void Apply(CoordinationRecord record)
    {
        switch (record)
        {
            case AgentRegisteredRecord agentRegistered:
                Agents[agentRegistered.AgentId] = agentRegistered;
                break;
            case AgentUnregisteredRecord agentUnregistered:
                Agents.TryRemove(agentUnregistered.AgentId, out _);
                break;
            case AgentHeartbeatRecord heartbeat:
                AgentLastSeen[heartbeat.AgentId] = heartbeat.Timestamp;
                break;
            case MessageSentRecord messageSent:
                Messages.Add(messageSent);
                break;
            case FileReadRecord fileRead:
                RecentReads.Add(fileRead);
                break;
            case FileWriteRecord fileWrite:
                RecentWrites.Add(fileWrite);
                break;
            case PreflightWarningRecord warning:
                RecentWarnings.Add(warning);
                break;
            case SubagentObservedRecord subagentEvent:
                SubagentEvents.Add(subagentEvent);
                break;
        }
    }
}
