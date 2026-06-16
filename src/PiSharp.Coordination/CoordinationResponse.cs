namespace PiSharp.Coordination;

public sealed record AgentEntry(string AgentId, int ProcessId, string? SessionId, string? ParentSessionId, string Cwd, DateTimeOffset RegisteredAt);

public sealed record RosterResponse(bool Ok, string Type, AgentEntry[] Agents);

public sealed record InboxMessage(string MessageId, string FromAgentId, string To, string Body, DateTimeOffset Timestamp);

public sealed record InboxResponse(bool Ok, string Type, InboxMessage[] Messages);

public sealed record BriefResponse(bool Ok, string Type, string Content);

public sealed record PreflightResponse(bool Ok, string Type, bool ShouldWarn, string Message);
