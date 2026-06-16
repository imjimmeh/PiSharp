using PiSharp.Abstractions.Errors;

namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Shell execution capability used by the harness.
/// Implementations must encode expected failures as Result errors rather than throwing.
/// </summary>
public interface IShell
{
    Task<Result<ShellResult, ExecutionError>> ExecAsync(
        string command,
        ExecutionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases shell resources. Implementations should be best-effort and should not throw.
    /// </summary>
    Task CleanupAsync(CancellationToken cancellationToken = default);
}
