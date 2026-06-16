namespace PiSharp.Tui.Interactive;

internal static class PromptFileReferenceScorer
{
    internal static int? Score(string path, string query, bool isDirectory)
    {
        if (string.IsNullOrEmpty(query)) return isDirectory ? 2 : 1;

        var fileName = Path.GetFileName(path.TrimEnd('/'));
        var lowerFileName = fileName.ToLowerInvariant();
        var lowerPath = path.ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();
        var score = 0;

        if (lowerFileName == lowerQuery) score = 100;
        else if (lowerFileName.StartsWith(lowerQuery, StringComparison.Ordinal)) score = 80;
        else if (lowerFileName.Contains(lowerQuery, StringComparison.Ordinal)) score = 50;
        else if (lowerPath.Contains(lowerQuery, StringComparison.Ordinal)) score = 30;
        else if (IsSubsequence(lowerQuery, lowerPath)) score = 10;

        if (score == 0) return null;
        if (isDirectory) score += 10;
        return score;
    }

    internal static bool IsSubsequence(string query, string text)
    {
        var queryIndex = 0;
        foreach (var c in text)
        {
            if (queryIndex < query.Length && c == query[queryIndex]) queryIndex++;
        }
        return queryIndex == query.Length;
    }
}
