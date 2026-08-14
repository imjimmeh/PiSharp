using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.Ast.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExecutionEnv"/> mirroring the harness's fake used by PiSharp.Tools.Tests,
/// so plugin tools are testable hermetically (no real filesystem, no subprocesses).
/// </summary>
internal sealed class FakeExecutionEnv : IExecutionEnv
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public FakeExecutionEnv(string cwd = "/repo")
    {
        Cwd = Normalize(cwd);
        _directories.Add(Cwd);
    }

    public string Cwd { get; }

    public void AddFile(string path, string content)
    {
        path = NormalizeAbsolute(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = content;
    }

    public string? ReadFileOrDefault(string path) => _files.GetValueOrDefault(NormalizeAbsolute(path));

    public bool HasFile(string path) => _files.ContainsKey(NormalizeAbsolute(path));

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<ShellResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.SpawnError, $"Shell execution is not available in tests: {command}")));

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default) => FileOk(NormalizeAbsolute(path));
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default) => FileOk(Normalize(Path.Combine(parts.ToArray())));

    public Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        return _files.TryGetValue(path, out var content) ? FileOk(content) : FileErr<string>(FileErrorCode.NotFound, $"File not found: {path}", path);
    }

    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        if (!_files.TryGetValue(path, out var content)) return FileErr<IReadOnlyList<string>>(FileErrorCode.NotFound, $"File not found: {path}", path);
        var lines = content.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        return FileOk<IReadOnlyList<string>>(maxLines is null ? lines : lines.Take(maxLines.Value).ToArray());
    }

    public async Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ReadTextFileAsync(path, cancellationToken).ConfigureAwait(false);
        return result.IsOk ? Result<byte[], FileError>.Ok(System.Text.Encoding.UTF8.GetBytes(result.Value)) : Result<byte[], FileError>.Err(result.Error);
    }

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = content;
        return FileOk(Unit.Value);
    }

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default) => WriteFileAsync(path, System.Text.Encoding.UTF8.GetString(content), cancellationToken);

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = _files.GetValueOrDefault(path, string.Empty) + content;
        return FileOk(Unit.Value);
    }

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default) => AppendFileAsync(path, System.Text.Encoding.UTF8.GetString(content), cancellationToken);

    public Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        if (_files.TryGetValue(path, out var content)) return FileOk(new FileSystemInfo(Path.GetFileName(path), path, FileKind.File, content.Length, DateTimeOffset.UtcNow));
        if (_directories.Contains(path)) return FileOk(new FileSystemInfo(Path.GetFileName(path), path, FileKind.Directory, 0, DateTimeOffset.UtcNow));
        return FileErr<FileSystemInfo>(FileErrorCode.NotFound, $"Path not found: {path}", path);
    }

    public Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        if (!_directories.Contains(path)) return FileErr<IReadOnlyList<FileSystemInfo>>(FileErrorCode.NotDirectory, $"Directory not found: {path}", path);
        var prefix = path.TrimEnd('/') + "/";
        var directories = _directories.Where(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry[prefix.Length..]).Where(rest => rest.Length > 0 && !rest.Contains('/'))
            .Select(rest => new FileSystemInfo(rest, Normalize(prefix + rest), FileKind.Directory, 0, DateTimeOffset.UtcNow));
        var files = _files.Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key[prefix.Length..]).Where(rest => rest.Length > 0 && !rest.Contains('/'))
            .Select(rest => new FileSystemInfo(rest, Normalize(prefix + rest), FileKind.File, _files[Normalize(prefix + rest)].Length, DateTimeOffset.UtcNow));
        return FileOk<IReadOnlyList<FileSystemInfo>>(directories.Concat(files).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default) => AbsolutePathAsync(path, cancellationToken);

    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        return FileOk(_files.ContainsKey(path) || _directories.Contains(path));
    }

    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default) { EnsureDirectory(path); return FileOk(Unit.Value); }
    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default) { path = NormalizeAbsolute(path); _files.Remove(path); _directories.Remove(path); return FileOk(Unit.Value); }
    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default) { var path = Normalize($"/tmp/{prefix}{Guid.NewGuid():N}"); EnsureDirectory(path); return FileOk(path); }
    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default) { var path = Normalize($"/tmp/{prefix}{Guid.NewGuid():N}{suffix}"); EnsureDirectory(Path.GetDirectoryName(path)); _files[path] = string.Empty; return FileOk(path); }
    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private string NormalizeAbsolute(string path) => Path.IsPathRooted(path) ? Normalize(path) : Normalize(Path.Combine(Cwd, path));

    private void EnsureDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = NormalizeAbsolute(path);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = path.StartsWith('/') ? "/" : string.Empty;
        foreach (var part in parts)
        {
            current = current is "/" or "" ? current + part : current + "/" + part;
            _directories.Add(Normalize(current));
        }
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/");
        return normalized.TrimEnd('/') switch { "" => "/", var value => value };
    }

    private static Task<Result<T, FileError>> FileOk<T>(T value) => Task.FromResult(Result<T, FileError>.Ok(value));
    private static Task<Result<T, FileError>> FileErr<T>(FileErrorCode code, string message, string? path = null) => Task.FromResult(Result<T, FileError>.Err(new FileError(code, message, path)));
}
