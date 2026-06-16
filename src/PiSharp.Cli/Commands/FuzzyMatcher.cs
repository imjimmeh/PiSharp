namespace PiSharp.Cli.Commands;

internal readonly record struct FuzzyMatch(bool Matches, double Score);

internal static class FuzzyMatcher
{
    public static FuzzyMatch Match(string query, string text)
    {
        var queryLower = query.ToLowerInvariant();
        var textLower = text.ToLowerInvariant();

        var primary = MatchNormalized(queryLower, textLower);
        if (primary.Matches) return primary;

        var swapped = SwapAlphaNumeric(queryLower);
        if (swapped is null) return primary;

        var swappedMatch = MatchNormalized(swapped, textLower);
        return swappedMatch.Matches ? swappedMatch with { Score = swappedMatch.Score + 5 } : primary;
    }

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

    private static FuzzyMatch MatchNormalized(string query, string text)
    {
        if (query.Length == 0) return new FuzzyMatch(true, 0);
        if (query.Length > text.Length) return new FuzzyMatch(false, 0);

        var queryIndex = 0;
        var score = 0d;
        var lastMatchIndex = -1;
        var consecutiveMatches = 0;

        for (var i = 0; i < text.Length && queryIndex < query.Length; i++)
        {
            if (text[i] != query[queryIndex]) continue;

            var isWordBoundary = i == 0 || char.IsWhiteSpace(text[i - 1]) || "-_./:".Contains(text[i - 1]);
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

        if (queryIndex < query.Length) return new FuzzyMatch(false, 0);
        if (query == text) score -= 100;
        return new FuzzyMatch(true, score);
    }

    private static string? SwapAlphaNumeric(string query)
    {
        var lettersThenDigits = SplitAlphaNumeric(query, lettersFirst: true);
        if (lettersThenDigits is not null) return lettersThenDigits.Value.digits + lettersThenDigits.Value.letters;
        var digitsThenLetters = SplitAlphaNumeric(query, lettersFirst: false);
        return digitsThenLetters is null ? null : digitsThenLetters.Value.letters + digitsThenLetters.Value.digits;
    }

    private static (string letters, string digits)? SplitAlphaNumeric(string query, bool lettersFirst)
    {
        var split = query.TakeWhile(c => lettersFirst ? char.IsLetter(c) : char.IsDigit(c)).Count();
        if (split == 0 || split == query.Length) return null;
        var first = query[..split];
        var second = query[split..];
        if (lettersFirst && first.All(char.IsLetter) && second.All(char.IsDigit)) return (first, second);
        if (!lettersFirst && first.All(char.IsDigit) && second.All(char.IsLetter)) return (second, first);
        return null;
    }
}
