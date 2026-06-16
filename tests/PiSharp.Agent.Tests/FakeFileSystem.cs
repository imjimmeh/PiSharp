using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.Agent.Tests;

internal sealed class FakeFileSystem : IExecutionEnv
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public FakeFileSystem(string cwd = "/repo")
    {
        Cwd = Normalize(cwd);
        _directories.Add(Cwd);
    }

    public string Cwd { get; }
    public int AppendCallCount { get; private set; }
    public Func<string, CancellationToken, Task>? BeforeReadTextFileAsync { get; set; }

    public string? ReadFileOrDefault(string path)
        => _files.GetValueOrDefault(Normalize(path));

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
        => Ok(Path.IsPathRooted(path) ? Normalize(path) : Normalize(Path.Combine(Cwd, path)));

    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => Ok(Normalize(Path.Combine(parts.ToArray())));

    public async Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        if (BeforeReadTextFileAsync is not null) await BeforeReadTextFileAsync(path, cancellationToken);
        return _files.TryGetValue(path, out var content)
            ? Result<string, FileError>.Ok(content)
            : Result<string, FileError>.Err(new FileError(FileErrorCode.NotFound, $"File not found: {path}", path));
    }

    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        if (!_files.TryGetValue(path, out var content)) return Err<IReadOnlyList<string>>(FileErrorCode.NotFound, $"File not found: {path}", path);
        var lines = content.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        return Ok<IReadOnlyList<string>>(maxLines is null ? lines : lines.Take(maxLines.Value).ToArray());
    }

    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
        => ReadTextFileAsync(path, cancellationToken).ContinueWith(task => task.Result.IsOk
            ? Result<byte[], FileError>.Ok(System.Text.Encoding.UTF8.GetBytes(task.Result.Value))
            : Result<byte[], FileError>.Err(task.Result.Error), cancellationToken);

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = content;
        return Ok(Unit.Value);
    }

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, System.Text.Encoding.UTF8.GetString(content), cancellationToken);

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        AppendCallCount++;
        _files[path] = _files.GetValueOrDefault(path, string.Empty) + content;
        return Ok(Unit.Value);
    }

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => AppendFileAsync(path, System.Text.Encoding.UTF8.GetString(content), cancellationToken);

    public Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        if (_files.TryGetValue(path, out var content)) return Ok(new FileSystemInfo(Path.GetFileName(path), path, FileKind.File, content.Length, DateTimeOffset.UtcNow));
        if (_directories.Contains(path)) return Ok(new FileSystemInfo(Path.GetFileName(path), path, FileKind.Directory, 0, DateTimeOffset.UtcNow));
        return Err<FileSystemInfo>(FileErrorCode.NotFound, $"Path not found: {path}", path);
    }

    public Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        if (!_directories.Contains(path)) return Err<IReadOnlyList<FileSystemInfo>>(FileErrorCode.NotFound, $"Directory not found: {path}", path);
        var prefix = path.TrimEnd('/') + "/";
        var directDirectories = _directories
            .Where(dir => dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(dir => dir[prefix.Length..])
            .Where(rest => rest.Length > 0 && !rest.Contains('/'))
            .Select(rest => Normalize(prefix + rest));
        var directFiles = _files.Keys
            .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(file => file[prefix.Length..])
            .Where(rest => rest.Length > 0 && !rest.Contains('/'))
            .Select(rest => Normalize(prefix + rest));
        var entries = directDirectories
            .Select(dir => new FileSystemInfo(Path.GetFileName(dir), dir, FileKind.Directory, 0, DateTimeOffset.UtcNow))
            .Concat(directFiles.Select(file => new FileSystemInfo(Path.GetFileName(file), file, FileKind.File, _files[file].Length, DateTimeOffset.UtcNow)))
            .OrderBy(entry => entry.Name)
            .ToArray();
        return Ok<IReadOnlyList<FileSystemInfo>>(entries);
    }

    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => AbsolutePathAsync(path, cancellationToken);

    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        return Ok(_files.ContainsKey(path) || _directories.Contains(path));
    }

    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
    {
        EnsureDirectory(path);
        return Ok(Unit.Value);
    }

    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        _files.Remove(path);
        _directories.Remove(path);
        return Ok(Unit.Value);
    }

    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
    {
        var path = Normalize($"/tmp/{prefix}{Guid.NewGuid():N}");
        EnsureDirectory(path);
        return Ok(path);
    }

    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
    {
        var path = Normalize($"/tmp/{prefix}{Guid.NewGuid():N}{suffix}");
        _files[path] = string.Empty;
        return Ok(path);
    }

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<ShellResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.SpawnError, $"No fake shell result queued for command: {command}")));

    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static Task<Result<T, FileError>> Ok<T>(T value) => Task.FromResult(Result<T, FileError>.Ok(value));
    private static Task<Result<T, FileError>> Err<T>(FileErrorCode code, string message, string? path = null) => Task.FromResult(Result<T, FileError>.Err(new FileError(code, message, path)));

    private void EnsureDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Normalize(path);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = path.StartsWith('/') ? "/" : string.Empty;
        foreach (var part in parts)
        {
            current = current == "/" || current.Length == 0 ? current + part : current + "/" + part;
            _directories.Add(Normalize(current));
        }
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').Replace("//", "/").TrimEnd('/') switch
        {
            "" => "/",
            var normalized => normalized
        };
}
