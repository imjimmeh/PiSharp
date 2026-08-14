using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.Ast.Host;

/// <summary>
/// Real-filesystem <see cref="IExecutionEnv"/> rooted at the harness working directory.
/// Shell execution is intentionally unavailable (the P30 tools are filesystem-only).
/// Implemented in the plugin rather than referencing PiSharp.Runtime so the plugin's
/// collectible ALC never loads a second copy of a non-shared assembly.
/// </summary>
public sealed class HostExecutionEnv(string cwd) : IExecutionEnv
{
    public string Cwd { get; } = Path.GetFullPath(cwd);

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
        => FileOk(Resolve(path));

    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => FileOk(Path.GetFullPath(Path.Combine(parts.ToArray())));

    public Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
        => Run(() => File.ReadAllText(Resolve(path)));

    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var lines = File.ReadAllLines(Resolve(path));
            return (IReadOnlyList<string>)(maxLines is null ? lines : lines.Take(maxLines.Value).ToArray());
        });

    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
        => Run(() => File.ReadAllBytes(Resolve(path)));

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => RunUnit(() =>
        {
            var absolute = Resolve(path);
            var parent = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.WriteAllBytes(absolute, content);
        });

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => RunUnit(() => File.AppendAllText(Resolve(path), content));

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => RunUnit(() =>
        {
            using var stream = new FileStream(Resolve(path), FileMode.Append, FileAccess.Write);
            stream.Write(content);
        });

    public Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var absolute = Resolve(path);
            if (Directory.Exists(absolute)) return ToInfo(absolute, FileKind.Directory);
            if (File.Exists(absolute)) return ToInfo(absolute, FileKind.File);
            throw new FileNotFoundException($"Path not found: {absolute}", absolute);
        });

    public Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var absolute = Resolve(path);
            if (!Directory.Exists(absolute)) throw new DirectoryNotFoundException($"Directory not found: {absolute}");
            var entries = Directory.GetFileSystemEntries(absolute)
                .Select(entry => ToInfo(entry, Directory.Exists(entry) ? FileKind.Directory : FileKind.File))
                .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return (IReadOnlyList<FileSystemInfo>)entries;
        });

    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => FileOk(Path.GetFullPath(Resolve(path)));

    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => FileOk(File.Exists(Resolve(path)) || Directory.Exists(Resolve(path)));

    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        => RunUnit(() => Directory.CreateDirectory(Resolve(path)));

    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
        => RunUnit(() =>
        {
            var absolute = Resolve(path);
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive);
            else if (File.Exists(absolute)) File.Delete(absolute);
        });

    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        });

    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + suffix);
            File.WriteAllText(path, string.Empty);
            return path;
        });

    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<ShellResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.ShellUnavailable, "Shell execution is not available for structural-editing tools.")));

    private string Resolve(string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path));

    private static FileSystemInfo ToInfo(string absolute, FileKind kind)
        => new(Path.GetFileName(absolute), absolute, kind, kind == FileKind.File ? new FileInfo(absolute).Length : 0, DateTimeOffset.UtcNow);

    private static async Task<Result<T, FileError>> Run<T>(Func<T> action)
    {
        try { return Result<T, FileError>.Ok(action()); }
        catch (Exception exception) { return Result<T, FileError>.Err(ToError(exception)); }
    }

    private static Task<Result<Unit, FileError>> RunUnit(Action action)
        => Run(() => { action(); return Unit.Value; });

    private static FileError ToError(Exception exception) => exception switch
    {
        FileNotFoundException ex => new FileError(FileErrorCode.NotFound, ex.Message, ex.FileName, ex),
        DirectoryNotFoundException ex => new FileError(FileErrorCode.NotFound, ex.Message, null, ex),
        UnauthorizedAccessException ex => new FileError(FileErrorCode.PermissionDenied, ex.Message, null, ex),
        IOException ex => new FileError(FileErrorCode.Unknown, ex.Message, null, ex),
        _ => new FileError(FileErrorCode.Unknown, exception.Message, null, exception),
    };

    private static Task<Result<T, FileError>> FileOk<T>(T value) => Task.FromResult(Result<T, FileError>.Ok(value));
}
