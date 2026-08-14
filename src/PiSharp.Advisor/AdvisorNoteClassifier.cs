namespace PiSharp.Advisor;

/// <summary>
/// Maps the advisor model's free-text note onto a review kind. A keyword
/// heuristic (per plan §9 Phase 3): blocker vocabulary wins, then concern
/// vocabulary, otherwise a plain note. Replaceable later by prompting the
/// model for structured output.
/// </summary>
public static class AdvisorNoteClassifier
{
    private static readonly string[] BlockerWords =
        ["blocker", "must", "cannot", "incorrect", "fatal", "fails", "rolled back"];

    private static readonly string[] ConcernWords =
        ["concern", "risk", "careful", "watch", "caution", "may", "could"];

    public static string Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "note";
        var lower = text.ToLowerInvariant();

        if (ContainsAny(lower, BlockerWords)) return "blocker";
        if (ContainsAny(lower, ConcernWords)) return "concern";
        return "note";
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (text.Contains(words[i], StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
