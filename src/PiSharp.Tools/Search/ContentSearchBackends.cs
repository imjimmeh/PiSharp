using System.Text.RegularExpressions;
using PiSharp.Abstractions.Environment;
using PiSharp.Tools.Shared;
using FileSystemInfo = PiSharp.Abstractions.Environment.FileSystemInfo;

namespace PiSharp.Tools.Search;

public interface IContentSearchBackend
{
    Task<ContentSearchResult?> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken);
}

public sealed class RipgrepContentSearchBackend(IExecutionEnv env) : IContentSearchBackend
{
    private readonly IExecutionEnv _env = env;

    public async Task<ContentSearchResult?> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string> { "rg", "--line-number", "--color", "never", "--max-count", request.Limit.ToString() };
        if (request.IgnoreCase) args.Add("--ignore-case");
        if (request.Literal) args.Add("--fixed-strings");
        if (request.Context > 0) { args.Add("--context"); args.Add(request.Context.ToString()); }
        if (request.Glob is not null) { args.Add("--glob"); args.Add(request.Glob); }
        args.Add(request.Pattern);
        args.Add(request.Root);
        var exec = await _env.ExecAsync(string.Join(" ", args.Select(ExternalSearchCommand.Quote)), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!exec.IsOk) return null;
        if (exec.Value.ExitCode == 1 && string.IsNullOrWhiteSpace(exec.Value.Stdout)) return new ContentSearchResult([]);
        if (exec.Value.ExitCode != 0) return null;
        if (string.IsNullOrWhiteSpace(exec.Value.Stdout)) return new ContentSearchResult([]);
        var truncation = Truncation.TruncateHead(exec.Value.Stdout.TrimEnd());
        return new ContentSearchResult([], Truncation: truncation.Truncated ? truncation : null, RawText: truncation.Content);
    }
}

public sealed class NativeContentSearchBackend(IExecutionEnv env) : IContentSearchBackend
{
    private readonly IExecutionEnv _env = env;

    public async Task<ContentSearchResult?> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken)
    {
        var matches = new List<ContentSearchMatch>();
        var linesTruncated = false;
        var files = await EnumerateFilesAsync(request.Root, cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            var relative = RelativePathFormatter.Format(request.Root, file.Path);
            if (request.Glob is not null && !GlobMatcher.IsMatch(request.Glob, relative)) continue;
            var read = await _env.ReadTextFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
            if (!read.IsOk) continue;
            var textLines = read.Value.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            var matchedLines = FindMatchingLineIndexes(textLines, request).ToArray();
            foreach (var matchIndex in matchedLines)
            {
                var start = Math.Max(0, matchIndex - request.Context);
                var end = Math.Min(textLines.Length - 1, matchIndex + request.Context);
                for (var i = start; i <= end; i++)
                {
                    var truncated = Truncation.TruncateLine(textLines[i]);
                    linesTruncated |= truncated.WasTruncated;
                    matches.Add(new ContentSearchMatch(relative, i + 1, truncated.Text));
                    if (matches.Count >= request.Limit) return new ContentSearchResult(matches, request.Limit, linesTruncated);
                }
            }
        }

        return new ContentSearchResult(matches, LinesTruncated: linesTruncated);
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

    private static IEnumerable<int> FindMatchingLineIndexes(IReadOnlyList<string> lines, ContentSearchRequest request)
    {
        if (request.Literal)
        {
            var comparison = request.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (var i = 0; i < lines.Count; i++) if (lines[i].Contains(request.Pattern, comparison)) yield return i;
            yield break;
        }

        var options = request.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        var regex = new Regex(request.Pattern, options);
        for (var i = 0; i < lines.Count; i++) if (regex.IsMatch(lines[i])) yield return i;
    }
}
