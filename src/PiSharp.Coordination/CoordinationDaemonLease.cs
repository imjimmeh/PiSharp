namespace PiSharp.Coordination;

public sealed record CoordinationDaemonLease(
    int ProcessId,
    string PipeName,
    string RepoRoot,
    DateTimeOffset StartedAt,
    DateTimeOffset LastCheckedAt);
