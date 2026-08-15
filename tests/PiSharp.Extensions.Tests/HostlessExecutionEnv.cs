using PiSharp.Abstractions;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Minimal <see cref="IExecutionEnv"/> for tests that only need a non-null environment
/// (every member except <see cref="Cwd"/> throws).
/// </summary>
internal sealed class HostlessExecutionEnv(string cwd) : IExecutionEnv
{
    public string Cwd { get; } = cwd;

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();
    public Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();
    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
        => Unsupported<Result<IReadOnlyList<string>, FileError>>();
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<byte[], FileError>>();
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();
    public Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<FileSystemInfo, FileError>>();
    public Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<IReadOnlyList<FileSystemInfo>, FileError>>();
    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();
    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<bool, FileError>>();
    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();
    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();
    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();
    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();
    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => Unsupported<Result<ShellResult, ExecutionError>>();
    public Task CleanupAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("Not implemented in the test fake."));
}
