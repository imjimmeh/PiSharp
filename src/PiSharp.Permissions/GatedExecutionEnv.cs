using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Extensions;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.Permissions;

/// <summary>
/// Wraps an <see cref="IExecutionEnv"/> so extension-initiated shell execution
/// (<c>ExecAsync</c>) passes through the permission capability gate. The gate is fail-closed:
/// a denial reason produces <see cref="ExecutionErrorCode.SpawnError"/> without invoking the
/// underlying shell. All <see cref="IFileSystem"/> members forward unmodified. When no gate is
/// installed (<see cref="CapabilityGates.ShellExec"/> is null) the call passes through — this is
/// the historic un-gated default, not a security stance: the permission extension always installs
/// the gate during <c>InitializeAsync</c>.
/// </summary>
public sealed class GatedExecutionEnv : IExecutionEnv
{
    private readonly IExecutionEnv _inner;

    /// <summary>
    /// Creates a gated wrapper. <paramref name="inner"/> is the real harness execution
    /// environment; <paramref name="explicitGate"/> overrides the static
    /// <see cref="CapabilityGates.ShellExec"/> seam (used by tests).
    /// </summary>
    public GatedExecutionEnv(IExecutionEnv inner, SpawnApproval? explicitGate = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        ExplicitGate = explicitGate;
    }

    /// <summary>
    /// Per-instance gate override; when null, <see cref="CapabilityGates.ShellExec"/> is consulted.
    /// An explicit (non-null) gate is authoritative: its decision is used as-is, so a null result
    /// means allow and never falls through to the static seam.
    /// </summary>
    public SpawnApproval? ExplicitGate { get; }

    private string? Evaluate(SpawnRequest request)
        => ExplicitGate is null
            ? CapabilityGates.EvaluateShell(request)
            : ExplicitGate.Invoke(request);

    public string Cwd => _inner.Cwd;

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(
        string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = new SpawnRequest("shell", command);
        var denial = Evaluate(request);
        if (denial is not null)
        {
            return Task.FromResult(Result.Err<ShellResult, ExecutionError>(
                new ExecutionError(ExecutionErrorCode.SpawnError,
                    $"Extension shell execution blocked by the permission gate: {denial}")));
        }
        return _inner.ExecAsync(command, options, cancellationToken);
    }

    // --- IFileSystem forwarding ---

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
        => _inner.AbsolutePathAsync(path, cancellationToken);
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => _inner.JoinPathAsync(parts, cancellationToken);
    public Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
        => _inner.ReadTextFileAsync(path, cancellationToken);
    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
        => _inner.ReadTextLinesAsync(path, maxLines, cancellationToken);
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
        => _inner.ReadBinaryFileAsync(path, cancellationToken);
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => _inner.WriteFileAsync(path, content, cancellationToken);
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => _inner.WriteFileAsync(path, content, cancellationToken);
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => _inner.AppendFileAsync(path, content, cancellationToken);
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => _inner.AppendFileAsync(path, content, cancellationToken);
    public Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        => _inner.GetFileInfoAsync(path, cancellationToken);
    public Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => _inner.ListDirectoryAsync(path, cancellationToken);
    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => _inner.GetCanonicalPathAsync(path, cancellationToken);
    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => _inner.ExistsAsync(path, cancellationToken);
    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        => _inner.CreateDirectoryAsync(path, recursive, cancellationToken);
    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(path, recursive, force, cancellationToken);
    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
        => _inner.CreateTempDirectoryAsync(prefix, cancellationToken);
    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
        => _inner.CreateTempFileAsync(prefix, suffix, cancellationToken);

    public Task CleanupAsync(CancellationToken cancellationToken = default)
        => ((IFileSystem)_inner).CleanupAsync(cancellationToken);
}
