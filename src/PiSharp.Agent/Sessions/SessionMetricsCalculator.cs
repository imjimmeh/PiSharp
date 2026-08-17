using System.Diagnostics;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Compaction;

namespace PiSharp.Agent.Sessions;

public sealed record SessionMetricsResult(
    string Cwd,
    string? GitBranch,
    int InputTokens,
    int OutputTokens,
    int CacheTokens,
    int TotalTokens,
    decimal TotalCost,
    double ContextPercent,
    bool ContextPercentKnown,
    int ContextWindow,
    bool AutoCompact);

public static class SessionMetricsCalculator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Branch, DateTimeOffset LastChecked)> _branchCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _branchRefreshInFlight = new(StringComparer.OrdinalIgnoreCase);

    public static SessionMetricsResult Calculate(
        IReadOnlyList<SessionTreeEntry>? sessionEntries,
        string cwd,
        int contextWindow,
        bool autoCompact = false,
        string? gitBranch = null)
    {
        gitBranch ??= ResolveGitBranch(cwd);
        var usage = UsageFromSessionEntries(sessionEntries);
        var input = usage.Sum(item => item.Input);
        var output = usage.Sum(item => item.Output);
        var cache = usage.Sum(item => item.CacheRead + item.CacheWrite);
        var total = usage.Sum(UsageTotal);
        var cost = usage.Sum(item => item.Cost?.Total ?? 0m);
        var contextUsage = EstimateContextTokens(sessionEntries);
        var contextTokens = contextUsage?.Tokens ?? total;
        var percent = contextWindow <= 0 ? 0 : Math.Min(100, contextTokens * 100d / contextWindow);

        return new SessionMetricsResult(
            ShortCwd(cwd),
            gitBranch,
            input,
            output,
            cache,
            total,
            cost,
            percent,
            contextUsage?.Known ?? true,
            contextWindow,
            autoCompact);
    }

    public static string? ResolveGitBranch(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var key = Path.GetFullPath(cwd);
        var now = DateTimeOffset.UtcNow;

        if (_branchCache.TryGetValue(key, out var cached))
        {
            if (now - cached.LastChecked > TimeSpan.FromSeconds(2) && _branchRefreshInFlight.TryAdd(key, 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var branch = await ResolveGitBranchAsync(key).ConfigureAwait(false);
                        _branchCache[key] = (branch, DateTimeOffset.UtcNow);
                    }
                    finally
                    {
                        _branchRefreshInFlight.TryRemove(key, out _);
                    }
                });
            }
            return cached.Branch;
        }

        _branchCache[key] = (null, now);
        if (_branchRefreshInFlight.TryAdd(key, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var branch = await ResolveGitBranchAsync(key).ConfigureAwait(false);
                    _branchCache[key] = (branch, DateTimeOffset.UtcNow);
                }
                finally
                {
                    _branchRefreshInFlight.TryRemove(key, out _);
                }
            });
        }

        return null;
    }

    public static async Task<string?> ResolveGitBranchAsync(string cwd, CancellationToken cancellationToken = default)
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
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var branch = (await outputTask.ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(branch) ? null : branch;
        }
        catch
        {
            return null;
        }
    }

    private static UsageInfo[] UsageFromSessionEntries(IReadOnlyList<SessionTreeEntry>? sessionEntries)
    {
        if (sessionEntries is null) return [];
        var usage = sessionEntries
            .OfType<MessageEntry>()
            .Select(entry => (entry.Message as AssistantMessage)?.Usage)
            .Where(item => item is not null)
            .Cast<UsageInfo>()
            .ToArray();
        return usage;
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
}
