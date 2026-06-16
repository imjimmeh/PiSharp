using PiSharp.Abstractions.Errors;

namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Filesystem capability used by the harness.
/// Implementations must encode expected failures as Result errors rather than throwing.
/// </summary>
public interface IFileSystem
{
    string Cwd { get; }

    Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default);

    Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default);

    Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);

    Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default);

    Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default);

    Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default);

    Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default);

    Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default);

    Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases filesystem resources. Implementations should be best-effort and should not throw.
    /// </summary>
    Task CleanupAsync(CancellationToken cancellationToken = default);
}
