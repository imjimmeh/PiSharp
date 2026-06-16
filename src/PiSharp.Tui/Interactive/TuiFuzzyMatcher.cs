namespace PiSharp.Tui.Interactive;

internal readonly record struct TuiFuzzyMatch(bool Matches, double Score);

internal static class TuiFuzzyMatcher
{
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string query, Func<T, string> text)
    {
        var tokens = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return items.ToArray();
        return items.Select(item =>
            {
                var total = 0d;
                foreach (var token in tokens)
                {
                    var match = Match(token, text(item));
                    if (!match.Matches) return (item, matches: false, score: 0d);
                    total += match.Score;
                }
                return (item, matches: true, score: total);
            })
            .Where(result => result.matches)
            .OrderBy(result => result.score)
            .Select(result => result.item)
            .ToArray();
    }

    private static TuiFuzzyMatch Match(string query, string text)
    {
        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();
        if (queryLower.Length == 0) return new TuiFuzzyMatch(true, 0);
        if (queryLower.Length > textLower.Length) return new TuiFuzzyMatch(false, 0);

        var queryIndex = 0;
        var score = 0d;
        var lastMatchIndex = -1;
        var consecutiveMatches = 0;
        for (var i = 0; i < textLower.Length && queryIndex < queryLower.Length; i++)
        {
            if (textLower[i] != queryLower[queryIndex]) continue;
            var isWordBoundary = i == 0 || char.IsWhiteSpace(textLower[i - 1]) || "-_./:".Contains(textLower[i - 1]);
            if (lastMatchIndex == i - 1)
            {
                consecutiveMatches++;
                score -= consecutiveMatches * 5;
            }
            else
            {
                consecutiveMatches = 0;
                if (lastMatchIndex >= 0) score += (i - lastMatchIndex - 1) * 2;
            }
            if (isWordBoundary) score -= 10;
            score += i * 0.1;
            lastMatchIndex = i;
            queryIndex++;
        }
        if (queryIndex < queryLower.Length) return new TuiFuzzyMatch(false, 0);
        if (queryLower == textLower) score -= 100;
        return new TuiFuzzyMatch(true, score);
    }
}
