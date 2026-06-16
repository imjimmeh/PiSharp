namespace PiSharp.Coordination;

public static class SoftConflictDetector
{
    public static ToolPreflightDecision PreflightWrite(
        CoordinationState state,
        string agentId,
        string path,
        DateTimeOffset timestamp)
    {
        var normalizedPath = FileToolActivityParser.NormalizePath(path, repoRoot: null);

        var mostRecentRead = state.RecentReads
            .Where(r => r.AgentId == agentId && NormalizedEqual(r.Path, normalizedPath))
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();

        if (mostRecentRead is null)
            return new ToolPreflightDecision(false, string.Empty);

        var conflictingWrites = state.RecentWrites
            .Where(w => w.AgentId != agentId
                        && NormalizedEqual(w.Path, normalizedPath)
                        && w.Timestamp > mostRecentRead.Timestamp)
            .OrderByDescending(w => w.Timestamp)
            .ToArray();

        if (conflictingWrites.Length == 0)
            return new ToolPreflightDecision(false, string.Empty);

        var mostRecentWarning = state.RecentWarnings
            .Where(w => w.AgentId == agentId && NormalizedEqual(w.Path, normalizedPath))
            .OrderByDescending(w => w.WarningTimestamp)
            .FirstOrDefault();

        var unwarnedConflicts = conflictingWrites
            .Where(w => mostRecentWarning is null || w.Timestamp > mostRecentWarning.WarningTimestamp)
            .ToArray();

        if (unwarnedConflicts.Length == 0)
            return new ToolPreflightDecision(false, string.Empty);

        var mostRecentConflict = unwarnedConflicts[0];
        return new ToolPreflightDecision(
            true,
            $"Warning: agent '{mostRecentConflict.AgentId}' edited '{normalizedPath}' "
            + $"at {mostRecentConflict.Timestamp:O} — after your last read. "
            + "re-read the file before editing, and re-run the tool only if the edit is still safe.");
    }

    private static bool NormalizedEqual(string a, string b)
    {
        return string.Equals(
            FileToolActivityParser.NormalizePath(a, repoRoot: null),
            FileToolActivityParser.NormalizePath(b, repoRoot: null),
            StringComparison.Ordinal);
    }
}
