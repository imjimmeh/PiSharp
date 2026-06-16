namespace PiSharp.Coordination;

public abstract record CoordinationRecord(string Type, DateTimeOffset Timestamp);

public sealed record AgentRegisteredRecord(
    string AgentId,
    int ProcessId,
    string? SessionId,
    string? ParentSessionId,
    string Cwd,
    DateTimeOffset Timestamp) : CoordinationRecord("agent_registered", Timestamp);

public sealed record AgentHeartbeatRecord(string AgentId, DateTimeOffset Timestamp) : CoordinationRecord("agent_heartbeat", Timestamp);

public sealed record AgentUnregisteredRecord(string AgentId, DateTimeOffset Timestamp) : CoordinationRecord("agent_unregistered", Timestamp);

public sealed record MessageSentRecord(
    string MessageId,
    string FromAgentId,
    string To,
    string Body,
    DateTimeOffset Timestamp) : CoordinationRecord("message_sent", Timestamp);

public sealed record FileReadRecord(string AgentId, string Path, DateTimeOffset Timestamp) : CoordinationRecord("file_read", Timestamp);

public sealed record FileWriteRecord(string AgentId, string Path, DateTimeOffset Timestamp) : CoordinationRecord("file_write", Timestamp);

public sealed record PreflightWarningRecord(
    string AgentId,
    string Path,
    string ConflictingAgentId,
    DateTimeOffset ConflictingTimestamp,
    DateTimeOffset WarningTimestamp) : CoordinationRecord("preflight_warning", WarningTimestamp);

public sealed record SubagentObservedRecord(
    string SubagentId,
    string? SubagentType,
    string? Description,
    string? Status,
    string EventName,
    double? DurationMs,
    int? ToolUses,
    int? InputTokens,
    int? OutputTokens,
    string? ParentSessionId,
    string Cwd,
    DateTimeOffset Timestamp) : CoordinationRecord("subagent_observed", Timestamp);
