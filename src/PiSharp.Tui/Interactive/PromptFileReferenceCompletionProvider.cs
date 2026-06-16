using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Tui.Interactive.Components;

namespace PiSharp.Tui.Interactive;

public sealed class PromptFileReferenceCompletionProvider
{
    private sealed class CountingFileReferenceFileSystem(IFileReferenceFileSystem inner, TuiProfilingCounters counters) : IFileReferenceFileSystem
    {
        public bool DirectoryExists(string path)
        {
            counters.Increment(TuiProfilingCounterNames.FileReferenceFileSystem);
            return inner.DirectoryExists(path);
        }

        public IEnumerable<string> EnumerateFileSystemEntries(string path)
        {
            counters.Increment(TuiProfilingCounterNames.FileReferenceFileSystem);
            return inner.EnumerateFileSystemEntries(path);
        }

        public string GetFullPath(string path)
        {
            counters.Increment(TuiProfilingCounterNames.FileReferenceFileSystem);
            return inner.GetFullPath(path);
        }

        public string GetRelativePath(string relativeTo, string path)
        {
            counters.Increment(TuiProfilingCounterNames.FileReferenceFileSystem);
            return inner.GetRelativePath(relativeTo, path);
        }
    }

    private sealed class CountingGitVisibilityService(IGitVisibilityService inner, TuiProfilingCounters counters) : IGitVisibilityService
    {
        public IEnumerable<string> EnumerateVisiblePaths(string baseDirectory, bool recursive)
        {
            counters.Increment(TuiProfilingCounterNames.FileReferenceGitVisibility);
            return inner.EnumerateVisiblePaths(baseDirectory, recursive);
        }
    }

    private readonly ILogger<PromptFileReferenceCompletionProvider> _logger;
    private const int MaxSuggestions = 20;
    private readonly PromptFileReferencePathResolver _pathResolver;
    private readonly IGitVisibilityService? _gitVisibility;
    private readonly IFileReferenceFileSystem _fileSystem;
    private readonly FileReferenceEntryEnumerator _enumerator;
    private readonly TuiProfilingCounters? _profilingCounters;
    private readonly Dictionary<string, IReadOnlyList<FileReferenceEntry>> _entryCache = new(PromptFileReferencePathResolver.PathComparer);

    public PromptFileReferenceCompletionProvider(string workingDirectory, ILoggerFactory? loggerFactory = null)
        : this(workingDirectory, GitVisibilityService.TryCreate(Path.GetFullPath(workingDirectory), loggerFactory?.CreateLogger(nameof(GitVisibilityService)) ?? NullLogger.Instance), loggerFactory: loggerFactory)
    {
    }

    internal PromptFileReferenceCompletionProvider(
        string workingDirectory,
        IGitVisibilityService? gitVisibility,
        ILoggerFactory? loggerFactory = null,
        TuiProfilingCounters? profilingCounters = null)
        : this(workingDirectory, gitVisibility, null, loggerFactory, profilingCounters)
    {
    }

    internal PromptFileReferenceCompletionProvider(
        string workingDirectory,
        IGitVisibilityService? gitVisibility,
        IFileReferenceFileSystem? fileSystem,
        ILoggerFactory? loggerFactory = null,
        TuiProfilingCounters? profilingCounters = null)
    {
        _logger = loggerFactory?.CreateLogger<PromptFileReferenceCompletionProvider>() ?? NullLogger<PromptFileReferenceCompletionProvider>.Instance;
        _pathResolver = new PromptFileReferencePathResolver(Path.GetFullPath(workingDirectory));
        _profilingCounters = profilingCounters;
        _gitVisibility = profilingCounters is null || gitVisibility is null ? gitVisibility : new CountingGitVisibilityService(gitVisibility, profilingCounters);
        _fileSystem = profilingCounters is null
            ? fileSystem ?? new SystemFileReferenceFileSystem()
            : new CountingFileReferenceFileSystem(fileSystem ?? new SystemFileReferenceFileSystem(), profilingCounters);
        _enumerator = new FileReferenceEntryEnumerator(_fileSystem, _logger);
    }

    public IReadOnlyList<PromptCompletion> Complete(string text, int cursorOffset)
    {
        _profilingCounters?.Increment(TuiProfilingCounterNames.FileReferenceCompletion);
        if (!TryExtractAtPrefix(text, cursorOffset, out var prefix, out var rawQuery, out var quoted)) return [];
        var scoped = _pathResolver.ResolveScopedQuery(rawQuery);
        var baseDirectory = scoped.BaseDirectory;
        if (!_pathResolver.IsUnderWorkingDirectory(baseDirectory)) return [];
        if (!_fileSystem.DirectoryExists(baseDirectory)) return [];

        var recursive = ShouldSearchRecursively(scoped.Query);
        var entries = GetEntries(baseDirectory, scoped.DisplayBase, recursive)
            .Select(entry => (entry, score: PromptFileReferenceScorer.Score(entry.DisplayPath, scoped.Query, entry.IsDirectory)))
            .Where(t => t.score.HasValue)
            .Select(t => (t.entry, t.score!.Value))
            .OrderByDescending(t => t.Item2)
            .ThenByDescending(t => t.entry.IsDirectory)
            .ThenBy(t => t.entry.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(t => ToCompletion(t.entry, prefix, quoted))
            .ToArray();
        return entries;
    }

    private static PromptCompletion ToCompletion(FileReferenceEntry entry, string prefix, bool quoted)
    {
        var completionPath = entry.IsDirectory ? entry.DisplayPath.TrimEnd('/') + "/" : entry.DisplayPath;
        var needsQuotes = quoted || completionPath.Contains(' ');
        var value = needsQuotes ? $"@\"{completionPath}\"" : $"@{completionPath}";
        return new PromptCompletion(
            value,
            Path.GetFileName(entry.DisplayPath.TrimEnd('/')) + (entry.IsDirectory ? "/" : string.Empty),
            entry.DisplayPath,
            prefix,
            AppendSpace: !entry.IsDirectory);
    }

    private static bool TryExtractAtPrefix(string text, int cursorOffset, out string prefix, out string rawQuery, out bool quoted)
        => PromptFileReferenceTokenExtractor.TryExtractAtPrefix(text, cursorOffset, out prefix, out rawQuery, out quoted);

    private static bool ShouldSearchRecursively(string query)
        => query.Length >= 2;

    private IReadOnlyList<FileReferenceEntry> GetEntries(string root, string displayBase, bool recursive)
    {
        var cacheKey = string.Concat(Path.GetFullPath(root), "\u001f", displayBase, "\u001f", recursive ? "1" : "0");
        if (_entryCache.TryGetValue(cacheKey, out var entries)) return entries;

        entries = EnumerateEntries(root, displayBase, recursive).ToArray();
        _entryCache[cacheKey] = entries;
        return entries;
    }

    private IEnumerable<FileReferenceEntry> EnumerateEntries(string root, string displayBase, bool recursive)
    {
        if (_gitVisibility is not null)
        {
            var gitEntries = _gitVisibility.EnumerateVisiblePaths(root, recursive)
                .Take(FileReferenceEntryEnumerator.MaxVisitedEntries)
                .ToArray();
            if (gitEntries.Length > 0)
            {
                foreach (var path in gitEntries)
                {
                    var entry = _enumerator.CreateEntry(path, root, displayBase);
                    if (entry.HasValue) yield return entry.Value;
                }
                yield break;
            }
        }

        foreach (var entry in _enumerator.EnumerateEntries(root, displayBase, recursive))
            yield return entry;
    }
}
