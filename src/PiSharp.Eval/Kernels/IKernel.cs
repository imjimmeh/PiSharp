namespace PiSharp.Eval.Kernels;

/// <summary>
/// Options used to start an eval kernel. Kernels are per-session, long-lived substrates:
/// state persists across <c>eval</c> tool calls until the kernel is reset or the runtime
/// tears down.
/// </summary>
public sealed record KernelStartOptions(
    string Cwd,
    IKernelToolBridge? ToolBridge = null,          // loopback; null disables tool re-entry
    IReadOnlyDictionary<string, string>? Environment = null,
    string? SessionId = null,
    KernelSnapshot? RestoreSnapshot = null);

/// <summary>Per-execution options. Nulls fall back to the kernel's configured defaults.</summary>
public sealed record KernelExecuteOptions(int? TimeoutMs = null, bool Reset = false);

/// <summary>
/// Result of a kernel execution. <see cref="Output"/> is the combined stdout/stderr text,
/// truncated to the kernel's output cap with a truncation marker.
/// </summary>
public sealed record KernelExecuteResult(
    string Output,            // combined stdout/stderr, truncated
    bool IsError,
    double DurationMs,
    bool TimedOut,
    bool WasReset,            // true when a poisoned kernel was reset before this execution
    KernelSnapshot? Snapshot = null);   // post-execution snapshot when the caller asked for one

/// <summary>
/// Serialized kernel state (prime-style). Variable values are stored as JSON; a value that
/// cannot be serialized marks that variable and the whole snapshot <see cref="Lossy"/> (the
/// name/type are kept, the value is dropped and restores as null with a warning).
/// </summary>
public sealed record KernelSnapshot(
    int SchemaVersion,
    string KernelName,
    string KernelVersion,
    DateTimeOffset CreatedAt,
    bool Lossy,
    IReadOnlyList<KernelVariableSnapshot> Variables,
    IReadOnlyList<string> Imports);

public sealed record KernelVariableSnapshot(string Name, string TypeName, string? Json, bool Lossy);

/// <summary>
/// Provider abstraction for a persistent eval kernel. Concrete kernels (C#, Python, ...) ship
/// as separate plugins and register factories via <see cref="EvalKernelRegistry"/>.
/// </summary>
public interface IKernel : IAsyncDisposable
{
    string Name { get; }                                  // "csharp" | "python" | ...
    string Language { get; }                              // "csharp" | "python"
    bool IsRunning { get; }
    Task StartAsync(KernelStartOptions options, CancellationToken ct = default);
    Task<KernelExecuteResult> ExecuteAsync(string code, KernelExecuteOptions? options = null,
        CancellationToken ct = default);
    Task<KernelSnapshot> SnapshotAsync(CancellationToken ct = default);
    Task RestoreAsync(KernelSnapshot snapshot, CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);
}


/// <summary>
/// Optional info surface kernels may implement for <c>/kernel</c> status rendering
/// (variable count, last-snapshot lossy flag).
/// </summary>
public interface IKernelInfo
{
    int VariableCount { get; }
    bool? LastSnapshotLossy { get; }
}