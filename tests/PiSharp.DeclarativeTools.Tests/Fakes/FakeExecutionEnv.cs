using System.Text;
using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.DeclarativeTools.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExecutionEnv"/> for script-tool tests: records executed
/// commands with their options, serves queued shell results, and stores files in
/// memory (including temp files created for args passing and output spill).
/// </summary>
internal sealed class FakeExecutionEnv : IExecutionEnv
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Queue<Func<string, ExecutionOptions?, CancellationToken, Task<Result<ShellResult, ExecutionError>>>> _shellResults = new();
    private readonly List<(string Command, ExecutionOptions Options)> _executed = [];
    private int _tempFileCounter;

    public FakeExecutionEnv(string cwd = "/repo")
    {
        Cwd = Normalize(cwd);
        _directories.Add(Cwd);
    }

    public string Cwd { get; }

    public IReadOnlyList<(string Command, ExecutionOptions Options)> ExecutedCommands => _executed;
    private readonly List<(string Path, string Content)> _writtenFiles = [];

    /// <summary>Append-only history of every file write (contents survive later deletes).</summary>
    public IReadOnlyList<(string Path, string Content)> WrittenFiles => _writtenFiles;

    public void AddFile(string path, string content)
    {
        path = NormalizeAbsolute(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = content;
    }

    public string? ReadFileOrDefault(string path) => _files.GetValueOrDefault(NormalizeAbsolute(path));

    public void EnqueueShellResult(string stdout, string stderr = "", int exitCode = 0)
        => _shellResults.Enqueue((command, options, _) =>
        {
            if (stdout.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(stdout);
                options?.OnOutputBytes?.Invoke(bytes, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                options?.OnStdout?.Invoke(stdout);
            }
            if (stderr.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(stderr);
                options?.OnOutputBytes?.Invoke(bytes, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                options?.OnStderr?.Invoke(stderr);
            }
            return Task.FromResult(Result<ShellResult, ExecutionError>.Ok(new ShellResult(stdout, stderr, exitCode)));
        });

    /// <summary>Streams chunks through <see cref="ExecutionOptions.OnOutputBytes"/> with a delay
    /// between them (used to observe throttled streaming updates).</summary>
    public void EnqueueStreamingResult(params string[] chunks)
        => _shellResults.Enqueue(async (command, options, _) =>
        {
            foreach (var chunk in chunks)
            {
                options?.OnOutputBytes?.Invoke(Encoding.UTF8.GetBytes(chunk), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                options?.OnStdout?.Invoke(chunk);
                await Task.Delay(120);
            }
            return Result<ShellResult, ExecutionError>.Ok(new ShellResult(string.Concat(chunks), string.Empty, 0));
        });

    public void EnqueueShellError(ExecutionErrorCode code, string message)
        => _shellResults.Enqueue((_, _, _) =>
            Task.FromResult(Result<ShellResult, ExecutionError>.Err(new ExecutionError(code, message))));

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Result<ShellResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.Aborted, "Operation aborted")));
        if (_shellResults.Count == 0)
            return Task.FromResult(Result<ShellResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.SpawnError, $"No fake shell result queued for command: {command}")));
        var effectiveOptions = options ?? new ExecutionOptions();
        _executed.Add((command, effectiveOptions));
        return _shellResults.Dequeue()(command, effectiveOptions, cancellationToken);
    }

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
        => FileOk(NormalizeAbsolute(path));

    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => FileOk(Normalize(Path.Combine(parts.ToArray())));

    public Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAbsolute(path);
        return _files.TryGetValue(normalized, out var content)
            ? FileOk(content)
            : FileErr<string>(FileErrorCode.NotFound, "File not found.", normalized);
    }

    public Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
    {
        var result = ReadTextFileAsync(path, cancellationToken).GetAwaiter().GetResult();
        if (result.IsErr) return Task.FromResult(Result<IReadOnlyList<string>, FileError>.Err(result.Error));
        var lines = result.Value.Split('\n');
        return FileOk<IReadOnlyList<string>>(maxLines is null ? lines : lines.Take(maxLines.Value).ToArray());
    }

    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = ReadTextFileAsync(path, cancellationToken).GetAwaiter().GetResult();
        return result.IsErr
            ? Task.FromResult(Result<byte[], FileError>.Err(result.Error))
            : FileOk(Encoding.UTF8.GetBytes(result.Value));
    }

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken);

    public Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        _writtenFiles.Add((path, Encoding.UTF8.GetString(content)));
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = Encoding.UTF8.GetString(content);
        return FileOk(Unit.Value);
    }

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => AppendFileAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken);

    public Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        path = NormalizeAbsolute(path);
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = (_files.TryGetValue(path, out var existing) ? existing : string.Empty) + Encoding.UTF8.GetString(content);
        return FileOk(Unit.Value);
    }

    public Task<Result<FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAbsolute(path);
        if (_files.TryGetValue(normalized, out var content))
            return FileOk(new FileSystemInfo(Path.GetFileName(normalized), normalized, FileKind.File, Encoding.UTF8.GetByteCount(content), DateTimeOffset.UtcNow));
        if (_directories.Contains(normalized))
            return FileOk(new FileSystemInfo(Path.GetFileName(normalized), normalized, FileKind.Directory, 0, DateTimeOffset.UtcNow));
        return FileErr<FileSystemInfo>(FileErrorCode.NotFound, "Path not found.", normalized);
    }

    public Task<Result<IReadOnlyList<FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAbsolute(path);
        var prefix = normalized.TrimEnd('/') + "/";
        var entries = _files.Keys
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.IndexOf('/', prefix.Length) == -1)
            .Select(p => new FileSystemInfo(Path.GetFileName(p), p, FileKind.File, Encoding.UTF8.GetByteCount(_files[p]), DateTimeOffset.UtcNow))
            .Cast<FileSystemInfo>()
            .ToList();
        entries.AddRange(_directories
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.IndexOf('/', prefix.Length) == -1)
            .Select(p => new FileSystemInfo(Path.GetFileName(p), p, FileKind.Directory, 0, DateTimeOffset.UtcNow)));
        return FileOk<IReadOnlyList<FileSystemInfo>>(entries);
    }

    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => FileOk(NormalizeAbsolute(path));

    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAbsolute(path);
        return FileOk(_files.ContainsKey(normalized) || _directories.Contains(normalized));
    }

    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
    {
        EnsureDirectory(NormalizeAbsolute(path));
        return FileOk(Unit.Value);
    }

    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAbsolute(path);
        _files.Remove(normalized);
        _directories.Remove(normalized);
        return FileOk(Unit.Value);
    }

    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
    {
        var path = Normalize($"/tmp/{prefix}{Guid.NewGuid():N}");
        EnsureDirectory(path);
        return FileOk(path);
    }

    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
    {
        var path = Normalize($"/tmp/{prefix}{++_tempFileCounter}-{Guid.NewGuid():N}{suffix}");
        EnsureDirectory(Path.GetDirectoryName(path));
        _files[path] = string.Empty;
        return FileOk(path);
    }

    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private string NormalizeAbsolute(string path)
        => Path.IsPathRooted(path) ? Normalize(path) : Normalize(Path.Combine(Cwd, path));

    private void EnsureDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var normalized = Normalize(path);
        _directories.Add(normalized);
        var parent = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(parent) && parent != normalized) EnsureDirectory(parent);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static Task<Result<T, FileError>> FileOk<T>(T value) => Task.FromResult(Result<T, FileError>.Ok(value));

    private static Task<Result<T, FileError>> FileErr<T>(FileErrorCode code, string message, string? path = null)
        => Task.FromResult(Result<T, FileError>.Err(new FileError(code, message, path)));
}
