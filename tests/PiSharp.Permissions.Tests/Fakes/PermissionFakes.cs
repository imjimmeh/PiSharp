using System.Text.Json;
using PiSharp.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Extensions;

namespace PiSharp.Permissions.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IExecutionEnv"/> fake: path resolution against <see cref="Cwd"/> and an
/// in-memory existing-file probe. The remaining file/shell surface throws (not exercised).
/// </summary>
public sealed class FakeExecutionEnv : IExecutionEnv
{
    private readonly HashSet<string> _existingFiles = new(StringComparer.OrdinalIgnoreCase);

    public string Cwd { get; set; } = "C:/project";

    public void AddExistingFile(string absolutePath) => _existingFiles.Add(Normalize(absolutePath));

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, CancellationToken cancellationToken = default)
    {
        var combined = Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path);
        return Task.FromResult(Result<string, FileError>.Ok(Path.GetFullPath(combined)));
    }

    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<string, FileError>.Ok(Path.GetFullPath(Path.Combine(parts.ToArray()))));

    public Task<Result<bool, FileError>> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<bool, FileError>.Ok(_existingFiles.Contains(Normalize(path))));

    public Task<Result<PiSharp.Abstractions.Environment.FileSystemInfo, FileError>> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Result<PiSharp.Abstractions.Environment.FileSystemInfo, FileError>.Err(new FileError(FileErrorCode.NotFound, "GetFileInfoAsync is not implemented in the fake.")));

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

    public Task<Result<IReadOnlyList<PiSharp.Abstractions.Environment.FileSystemInfo>, FileError>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<IReadOnlyList<PiSharp.Abstractions.Environment.FileSystemInfo>, FileError>>();

    public Task<Result<string, FileError>> GetCanonicalPathAsync(string path, CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();

    public Task<Result<Unit, FileError>> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();

    public Task<Result<Unit, FileError>> RemoveAsync(string path, bool recursive = false, bool force = false, CancellationToken cancellationToken = default)
        => Unsupported<Result<Unit, FileError>>();

    public Task<Result<string, FileError>> CreateTempDirectoryAsync(string prefix = "tmp-", CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();

    public Task<Result<string, FileError>> CreateTempFileAsync(string prefix = "", string suffix = "", CancellationToken cancellationToken = default)
        => Unsupported<Result<string, FileError>>();

    public Task CleanupAsync(CancellationToken cancellationToken = default)
        => UnsupportedTask();

    public Task<Result<ShellResult, ExecutionError>> ExecAsync(string command, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
        => Unsupported<Result<ShellResult, ExecutionError>>();

    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("Not implemented in the fake."));

    private static Task UnsupportedTask() => Task.FromException(new NotSupportedException("Not implemented in the fake."));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

/// <summary>
/// Scriptable <see cref="IExtensionUi"/> fake for the <c>permission_request</c> approval lane:
/// the next verdict is popped from the enqueued queue (defaulting to a failed result); requests
/// are captured on <see cref="Requests"/>.
/// </summary>
public sealed class FakeApprovalUi : IExtensionUi
{
    private readonly Queue<string?> _verdicts = new();

    public IReadOnlyList<ExtensionUiRequest> Requests { get; } = new List<ExtensionUiRequest>();

    public void EnqueueVerdict(string? value) => _verdicts.Enqueue(value);

    public Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
    {
        ((List<ExtensionUiRequest>)Requests).Add(request);
        var verdict = _verdicts.Count > 0 ? _verdicts.Dequeue() : null;
        return Task.FromResult(verdict is null
            ? new ExtensionUiResult(false, Error: "Unsupported extension UI request kind.")
            : new ExtensionUiResult(true, verdict));
    }

    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Throws on every call — models hosts whose UI surface throws (e.g. NoExtensionUi).</summary>
public sealed class ThrowingExtensionUi : IExtensionUi
{
    public static ThrowingExtensionUi Instance { get; } = new();

    private ThrowingExtensionUi() { }

    public Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<ExtensionUiResult>(new NotSupportedException("Extension UI is not available in this mode."));

    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Extension UI is not available in this mode."));

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(new NotSupportedException("Extension UI is not available in this mode."));

    public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
        => Task.FromException<string?>(new NotSupportedException("Extension UI is not available in this mode."));

    public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => Task.FromException<string?>(new NotSupportedException("Extension UI is not available in this mode."));

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Extension UI is not available in this mode."));

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Extension UI is not available in this mode."));
}
