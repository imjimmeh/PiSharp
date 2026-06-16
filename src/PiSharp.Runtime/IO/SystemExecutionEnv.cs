using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;

namespace PiSharp.Runtime.IO;

public sealed class SystemExecutionEnv(string cwd, ILoggerFactory? loggerFactory = null) : IExecutionEnv
{
    private readonly ILogger _logger = loggerFactory?.CreateLogger<SystemExecutionEnv>() ?? NullLogger<SystemExecutionEnv>.Instance;
    public string Cwd { get; } = Path.GetFullPath(cwd);

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
        => FileResult(() => Resolve(path), path);

    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => FileResult(() => Path.Combine(parts.ToArray()));

    public async Task<Result<string, FileError>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
        => await FileResultAsync(async () => await File.ReadAllTextAsync(Resolve(path), cancellationToken), path);

    public async Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(string path, int? maxLines = null, CancellationToken cancellationToken = default)
        => await FileResultAsync<IReadOnlyList<string>>(async () =>
        {
            var lines = await File.ReadAllLinesAsync(Resolve(path), cancellationToken);
            return maxLines is null ? lines : lines.Take(maxLines.Value).ToArray();
        }, path);

    public async Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, CancellationToken cancellationToken = default)
        => await FileResultAsync(async () => await File.ReadAllBytesAsync(Resolve(path), cancellationToken), path);

    public async Task<Result<Unit, FileError>> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => await FileResultAsync(async () =>
        {
            var resolved = Resolve(path);
            if (ShouldLogSessionWrite(resolved))
            {
                var directoryExists = Directory.Exists(Path.GetDirectoryName(resolved) ?? string.Empty);
                var fileExists = File.Exists(resolved);
                _logger.LogDebug(
                    "System execution env write starting path={Path} directoryExists={DirectoryExists} fileExists={FileExists} contentLength={ContentLength}",
                    resolved,
                    directoryExists,
                    fileExists,
                    content.Length);

                var bytes = Encoding.UTF8.GetBytes(content);
                _logger.LogDebug(
                    "System execution env write encoded path={Path} contentLength={ContentLength} byteLength={ByteLength}",
                    resolved,
                    content.Length,
                    bytes.Length);

                var stream = new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.Read);
                _logger.LogDebug(
                    "System execution env write stream opened path={Path} fileExists={FileExists}",
                    resolved,
                    File.Exists(resolved));

                try
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    _logger.LogDebug(
                        "System execution env write bytes written path={Path} byteLength={ByteLength}",
                        resolved,
                        bytes.Length);

                    _logger.LogDebug(
                        "System execution env write skipping explicit flush path={Path} position={Position} length={Length}",
                        resolved,
                        stream.Position,
                        stream.Length);
                }
                finally
                {
                    _logger.LogDebug("System execution env write disposing stream path={Path}", resolved);
                    await stream.DisposeAsync();
                    _logger.LogDebug(
                        "System execution env write disposed stream path={Path} fileExists={FileExists}",
                        resolved,
                        File.Exists(resolved));
                }

                _logger.LogDebug(
                    "System execution env write completed path={Path} fileExists={FileExists} contentLength={ContentLength}",
                    resolved,
                    File.Exists(resolved),
                    content.Length);

                return Unit.Value;
            }

            await File.WriteAllTextAsync(resolved, content, cancellationToken);

            return Unit.Value;
        }, path);

    public async Task<Result<Unit, FileError>> WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => await FileResultAsync(async () => { await File.WriteAllBytesAsync(Resolve(path), content, cancellationToken); return Unit.Value; }, path);

    public async Task<Result<Unit, FileError>> AppendFileAsync(string path, string content, CancellationToken cancellationToken = default)
        => await FileResultAsync(async () => { await File.AppendAllTextAsync(Resolve(path), content, cancellationToken); return Unit.Value; }, path);

    public async Task<Result<Unit, FileError>> AppendFileAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        => await FileResultAsync(async () =>
        {
            await using var stream = File.Open(Resolve(path), FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(content, cancellationToken);
            return Unit.Value;
        }, path);

    public Task<Result<Abstractions.Environment.FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        => FileResult(() => Info(Resolve(path)), path);

    public Task<Result<IReadOnlyList<Abstractions.Environment.FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => FileResult<IReadOnlyList<Abstractions.Environment.FileSystemInfo>>(() => Directory.EnumerateFileSystemEntries(Resolve(path)).Select(Info).ToArray(), path);

    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => AbsolutePathAsync(path, cancellationToken);

    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => FileResult(() => File.Exists(Resolve(path)) || Directory.Exists(Resolve(path)), path);

    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        => FileResult(() => { Directory.CreateDirectory(Resolve(path)); return Unit.Value; }, path);

    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
        => FileResult(() =>
        {
            var full = Resolve(path);
            if (Directory.Exists(full)) Directory.Delete(full, recursive);
            else if (File.Exists(full)) File.Delete(full);
            return Unit.Value;
        }, path);

    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
        => FileResult(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        });

    public async Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
        => await FileResultAsync(async () =>
        {
            var file = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + suffix);
            await File.WriteAllTextAsync(file, string.Empty, cancellationToken);
            return file;
        });

    private static bool ShouldLogSessionWrite(string resolvedPath)
        => resolvedPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
           && resolvedPath.Contains("\\agent\\sessions\\", StringComparison.OrdinalIgnoreCase);

    public async Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = options?.Timeout is null ? null : new CancellationTokenSource(options.Timeout.Value);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts?.Token ?? CancellationToken.None);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-lc \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = options?.Cwd is null ? Cwd : Resolve(options.Cwd),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (options?.Environment is not null)
            {
                foreach (var (key, value) in options.Environment) startInfo.Environment[key] = value;
            }

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => HandleLine(e.Data, stdout, options?.OnStdout, options?.OnOutputBytes, linkedCts.Token);
            process.ErrorDataReceived += (_, e) => HandleLine(e.Data, stderr, options?.OnStderr, options?.OnOutputBytes, linkedCts.Token);

            if (!process.Start()) return Result.Err<ShellResult, ExecutionError>(new ExecutionError(ExecutionErrorCode.SpawnError, "Failed to start command."));
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(linkedCts.Token);
            return Result.Ok<ShellResult, ExecutionError>(new ShellResult(stdout.ToString(), stderr.ToString(), process.ExitCode));
        }
        catch (OperationCanceledException ex) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            return Result.Err<ShellResult, ExecutionError>(new ExecutionError(ExecutionErrorCode.Timeout, "Command timed out.", ex));
        }
        catch (OperationCanceledException ex)
        {
            return Result.Err<ShellResult, ExecutionError>(new ExecutionError(ExecutionErrorCode.Aborted, "Command aborted.", ex));
        }
        catch (Exception ex)
        {
            return Result.Err<ShellResult, ExecutionError>(new ExecutionError(ExecutionErrorCode.SpawnError, ex.Message, ex));
        }
    }

    public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private string Resolve(string path) => Path.GetFullPath(path, Cwd);

    private static void HandleLine(string? line, StringBuilder capture, Action<string>? callback, ShellOutputBytesCallback? bytesCallback, CancellationToken cancellationToken)
    {
        if (line is null) return;
        capture.AppendLine(line);
        callback?.Invoke(line);
        if (bytesCallback is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            bytesCallback(bytes, cancellationToken).GetAwaiter().GetResult();
        }
    }

    private static Abstractions.Environment.FileSystemInfo Info(string path)
    {
        var attr = File.GetAttributes(path);
        var kind = attr.HasFlag(FileAttributes.Directory) ? FileKind.Directory : FileKind.File;
        var size = kind == FileKind.File ? new System.IO.FileInfo(path).Length : 0;
        return new Abstractions.Environment.FileSystemInfo(Path.GetFileName(path), path, kind, size, File.GetLastWriteTimeUtc(path));
    }

    private static Task<Result<T, FileError>> FileResult<T>(Func<T> action, string? path = null)
    {
        try { return Task.FromResult(Result.Ok<T, FileError>(action())); }
        catch (Exception ex) { return Task.FromResult(Result.Err<T, FileError>(Map(ex, path))); }
    }

    private static async Task<Result<T, FileError>> FileResultAsync<T>(Func<Task<T>> action, string? path = null)
    {
        try { return Result.Ok<T, FileError>(await action()); }
        catch (Exception ex) { return Result.Err<T, FileError>(Map(ex, path)); }
    }

    private static FileError Map(Exception ex, string? path) => ex switch
    {
        OperationCanceledException => new FileError(FileErrorCode.Aborted, ex.Message, path, ex),
        FileNotFoundException or DirectoryNotFoundException => new FileError(FileErrorCode.NotFound, ex.Message, path, ex),
        UnauthorizedAccessException => new FileError(FileErrorCode.PermissionDenied, ex.Message, path, ex),
        _ => new FileError(FileErrorCode.Unknown, ex.Message, path, ex)
    };
}
