using PiSharp.Abstractions.Environment;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.Tools.Search;

public interface IFileSearchBackend
{
    Task<FileSearchResult?> FindAsync(FileSearchRequest request, CancellationToken cancellationToken);
}

public sealed class FdFileSearchBackend(IExecutionEnv env) : IFileSearchBackend
{
    private readonly IExecutionEnv _env = env;

    public async Task<FileSearchResult?> FindAsync(FileSearchRequest request, CancellationToken cancellationToken)
    {
        var exec = await _env.ExecAsync($"fd --type f --glob --max-results {request.Limit} {ExternalSearchCommand.Quote(request.Pattern)} {ExternalSearchCommand.Quote(request.Root)}", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exec.IsOk || exec.Value.ExitCode != 0 || string.IsNullOrWhiteSpace(exec.Value.Stdout)) return null;
        var truncation = Shared.Truncation.TruncateHead(exec.Value.Stdout.TrimEnd());
        return new FileSearchResult([], Truncation: truncation.Truncated ? truncation : null, RawText: truncation.Content);
    }
}

public sealed class NativeFileSearchBackend(IExecutionEnv env) : IFileSearchBackend
{
    private readonly IExecutionEnv _env = env;

    public async Task<FileSearchResult?> FindAsync(FileSearchRequest request, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var files = await EnumerateFilesAsync(request.Root, cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            var relative = RelativePathFormatter.Format(request.Root, file.Path);
            if (!GlobMatcher.IsMatch(request.Pattern, relative)) continue;
            results.Add(relative);
            if (results.Count >= request.Limit) return new FileSearchResult(results, request.Limit);
        }

        return new FileSearchResult(results);
    }

    private async Task<IReadOnlyList<FileSystemInfo>> EnumerateFilesAsync(string path, CancellationToken cancellationToken)
    {
        var info = await _env.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsOk && info.Value.Kind == FileKind.File) return [info.Value];
        var entries = await _env.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        if (!entries.IsOk) return [];
        var files = new List<FileSystemInfo>();
        foreach (var entry in entries.Value)
        {
            if (entry.Kind == FileKind.File) files.Add(entry);
            else if (entry.Kind == FileKind.Directory) files.AddRange(await EnumerateFilesAsync(entry.Path, cancellationToken).ConfigureAwait(false));
        }
        return files;
    }
}
