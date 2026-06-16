namespace PiSharp.Coordination;

public sealed record AgentRegistration(string AgentId, int ProcessId, string? SessionId, string? ParentSessionId, string Cwd);

public sealed record CoordinationEndpoint(string PipeName);
