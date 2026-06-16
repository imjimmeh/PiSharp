using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Compaction;
using PiSharp.Agent.Sessions;

namespace PiSharp.Tui.Interactive;

public sealed record TuiFooterSnapshot(
    string Cwd,
    string? GitBranch,
    int InputTokens,
    int OutputTokens,
    int CacheTokens,
    int TotalTokens,
    decimal TotalCost,
    double ContextPercent,
    int ContextWindow,
    bool AutoCompact,
    IReadOnlyDictionary<string, string> ExtensionStatuses)
{
    public bool ContextPercentKnown { get; init; } = true;
}

public sealed class TuiFooterSnapshotProvider
{
    private readonly Func<string, string?> _resolveGitBranch;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _gitBranchCacheDuration;
    private readonly Dictionary<string, GitBranchCacheEntry> _gitBranchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TuiFooterSnapshotProvider> _logger;

    public TuiFooterSnapshotProvider(
        Func<string, string?>? resolveGitBranch = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? gitBranchCacheDuration = null,
        ILoggerFactory? loggerFactory = null)
    {
        _resolveGitBranch = resolveGitBranch ?? FooterDataProvider.ResolveGitBranch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _gitBranchCacheDuration = gitBranchCacheDuration ?? TimeSpan.FromSeconds(2);
        _logger = loggerFactory?.CreateLogger<TuiFooterSnapshotProvider>() ?? NullLogger<TuiFooterSnapshotProvider>.Instance;
    }

    public TuiFooterSnapshot CreateSnapshot(TuiRenderState state, string cwd, bool autoCompact = false)
        => CreateSnapshotFromSessionEntries(state, cwd, sessionEntries: null, autoCompact);

    public TuiFooterSnapshot CreateSnapshotFromSessionEntries(
        TuiRenderState state,
        string cwd,
        IReadOnlyList<SessionTreeEntry>? sessionEntries,
        bool autoCompact = false)
    {
        var snapshot = FooterDataProvider.CreateSnapshotFromSessionEntries(state, cwd, sessionEntries, autoCompact, GetCachedGitBranch(cwd));
        _logger.LogDebug(
            "Footer snapshot created cwd={Cwd} branch={GitBranch} totalTokens={TotalTokens} contextPercent={ContextPercent} contextKnown={ContextKnown} autoCompact={AutoCompact}",
            cwd,
            snapshot.GitBranch,
            snapshot.TotalTokens,
            snapshot.ContextPercent,
            snapshot.ContextPercentKnown,
            snapshot.AutoCompact);
        return snapshot;
    }

    private string? GetCachedGitBranch(string cwd)
    {
        var key = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd;
        var now = _clock();
        if (_gitBranchCache.TryGetValue(key, out var cached) && now - cached.CreatedAt <= _gitBranchCacheDuration)
        {
            _logger.LogDebug("Footer git branch cache hit cwd={Cwd} branch={GitBranch}", key, cached.Branch);
            return cached.Branch;
        }

        var branch = _resolveGitBranch(key);
        _gitBranchCache[key] = new GitBranchCacheEntry(branch, now);
        _logger.LogDebug("Footer git branch cache refresh cwd={Cwd} branch={GitBranch}", key, branch);
        return branch;
    }

    private sealed record GitBranchCacheEntry(string? Branch, DateTimeOffset CreatedAt);
}

public static class FooterDataProvider
{
    public static string FormatTokenCount(int count)
    {
        if (count < 1000) return count.ToString();
        if (count < 10000) return $"{count / 1000d:0.0}k";
        if (count < 1000000) return $"{Math.Round(count / 1000d):0}k";
        if (count < 10000000) return $"{count / 1000000d:0.0}M";
        return $"{Math.Round(count / 1000000d):0}M";
    }

    public static TuiFooterSnapshot CreateSnapshot(TuiRenderState state, string cwd, bool autoCompact = false)
        => CreateSnapshotFromSessionEntries(state, cwd, sessionEntries: null, autoCompact);

    public static TuiFooterSnapshot CreateSnapshotFromSessionEntries(
        TuiRenderState state,
        string cwd,
        IReadOnlyList<SessionTreeEntry>? sessionEntries,
        bool autoCompact = false)
        => CreateSnapshotFromSessionEntries(state, cwd, sessionEntries, autoCompact, ResolveGitBranch(cwd));

    internal static TuiFooterSnapshot CreateSnapshotFromSessionEntries(
        TuiRenderState state,
        string cwd,
        IReadOnlyList<SessionTreeEntry>? sessionEntries,
        bool autoCompact,
        string? gitBranch)
    {
        var usage = UsageFromSessionEntries(sessionEntries) ?? UsageFromTranscript(state);
        var input = usage.Sum(item => item.Input);
        var output = usage.Sum(item => item.Output);
        var cache = usage.Sum(item => item.CacheRead + item.CacheWrite);
        var total = usage.Sum(UsageTotal);
        var cost = usage.Sum(item => item.Cost?.Total ?? 0m);
        var contextUsage = EstimateContextTokens(sessionEntries);
        var contextTokens = contextUsage?.Tokens ?? total;
        var percent = state.ContextWindow <= 0 ? 0 : Math.Min(100, contextTokens * 100d / state.ContextWindow);
        return new TuiFooterSnapshot(ShortCwd(cwd), gitBranch, input, output, cache, total, cost, percent, state.ContextWindow, autoCompact, state.Statuses)
        {
            ContextPercentKnown = contextUsage?.Known ?? true
        };
    }

    private static UsageInfo[] UsageFromTranscript(TuiRenderState state)
        => state.Transcript.Select(item => item.Usage).Where(item => item is not null).Cast<UsageInfo>().ToArray();

    private static UsageInfo[]? UsageFromSessionEntries(IReadOnlyList<SessionTreeEntry>? sessionEntries)
    {
        if (sessionEntries is null) return null;
        var usage = sessionEntries
            .OfType<MessageEntry>()
            .Select(entry => (entry.Message as AssistantMessage)?.Usage)
            .Where(item => item is not null)
            .Cast<UsageInfo>()
            .ToArray();
        return usage.Length == 0 ? null : usage;
    }

    private static (int Tokens, bool Known)? EstimateContextTokens(IReadOnlyList<SessionTreeEntry>? sessionEntries)
    {
        if (sessionEntries is null) return null;
        var latestCompactionIndex = LatestCompactionIndex(sessionEntries);
        if (latestCompactionIndex is not null && !HasPostCompactionUsage(sessionEntries, latestCompactionIndex.Value))
        {
            return (0, false);
        }

        var context = Session<JsonlSessionMetadata>.BuildSessionContext(sessionEntries);
        var messages = context.Messages;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index] is not AssistantMessage { Usage: { } usage } assistant) continue;
            if (assistant.StopReason is "aborted" or "error") continue;
            return (UsageTotal(usage) + messages.Skip(index + 1).Sum(CompactionService.EstimateTokens), true);
        }

        return (messages.Sum(CompactionService.EstimateTokens), true);
    }

    private static int? LatestCompactionIndex(IReadOnlyList<SessionTreeEntry> sessionEntries)
    {
        for (var index = sessionEntries.Count - 1; index >= 0; index--)
        {
            if (sessionEntries[index] is CompactionEntry) return index;
        }

        return null;
    }

    private static bool HasPostCompactionUsage(IReadOnlyList<SessionTreeEntry> sessionEntries, int compactionIndex)
    {
        for (var index = sessionEntries.Count - 1; index > compactionIndex; index--)
        {
            if (sessionEntries[index] is not MessageEntry { Message: AssistantMessage { Usage: { } usage } assistant }) continue;
            if (assistant.StopReason is "aborted" or "error") continue;
            return UsageTotal(usage) > 0;
        }

        return false;
    }

    private static int UsageTotal(UsageInfo usage)
        => usage.TotalTokens == 0 ? usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite : usage.TotalTokens;

    private static string ShortCwd(string cwd)
        => string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public static string? ResolveGitBranch(string cwd)
    {
        try
        {
            var start = new ProcessStartInfo("git", "branch --show-current")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is null) return null;
            process.WaitForExit(250);
            var branch = process.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(branch) ? null : branch;
        }
        catch
        {
            return null;
        }
    }
}
