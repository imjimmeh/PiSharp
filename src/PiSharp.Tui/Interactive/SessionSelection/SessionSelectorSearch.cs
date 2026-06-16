using System.Text.RegularExpressions;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Tui.Interactive.SessionSelection;

public enum SessionSelectorSortMode
{
    Threaded,
    Recent,
    Relevance
}

public enum SessionSelectorNameFilter
{
    All,
    Named
}

public static class SessionSelectorSearch
{
    public static IReadOnlyList<JsonlSessionMetadata> FilterAndSort(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string query,
        SessionSelectorSortMode sortMode = SessionSelectorSortMode.Relevance,
        SessionSelectorNameFilter nameFilter = SessionSelectorNameFilter.All)
    {
        var nameFiltered = nameFilter == SessionSelectorNameFilter.All
            ? sessions
            : sessions.Where(session => !string.IsNullOrWhiteSpace(session.Name)).ToArray();
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return nameFiltered.OrderByDescending(session => session.ModifiedAt).ToArray();

        var parsed = ParsedSearchQuery.Parse(trimmed);
        if (parsed.Error is not null) return [];

        if (sortMode == SessionSelectorSortMode.Recent)
        {
            return nameFiltered
                .Where(session => parsed.Match(session).Matches)
                .ToArray();
        }

        return nameFiltered
            .Select(session => (Session: session, Match: parsed.Match(session)))
            .Where(result => result.Match.Matches)
            .OrderBy(result => result.Match.Score)
            .ThenByDescending(result => result.Session.ModifiedAt)
            .Select(result => result.Session)
            .ToArray();
    }

    private static string SearchText(JsonlSessionMetadata session)
        => $"{session.Id} {session.Name ?? string.Empty} {session.FirstMessage} {session.AllMessagesText} {session.Cwd}";

    private sealed record ParsedSearchQuery(SearchMode Mode, IReadOnlyList<SearchToken> Tokens, Regex? Regex, string? Error)
    {
        public static ParsedSearchQuery Parse(string query)
        {
            if (query.StartsWith("re:", StringComparison.Ordinal))
            {
                var pattern = query[3..].Trim();
                if (pattern.Length == 0) return new ParsedSearchQuery(SearchMode.Regex, [], null, "Empty regex");
                try { return new ParsedSearchQuery(SearchMode.Regex, [], new Regex(pattern, RegexOptions.IgnoreCase), null); }
                catch (ArgumentException ex) { return new ParsedSearchQuery(SearchMode.Regex, [], null, ex.Message); }
            }

            return new ParsedSearchQuery(SearchMode.Tokens, ParseTokens(query), null, null);
        }

        public MatchResult Match(JsonlSessionMetadata session)
        {
            var text = SearchText(session);
            if (Mode == SearchMode.Regex)
            {
                if (Regex is null) return new MatchResult(false, 0);
                var match = Regex.Match(text);
                return match.Success ? new MatchResult(true, match.Index * 0.1) : new MatchResult(false, 0);
            }

            if (Tokens.Count == 0) return new MatchResult(true, 0);
            var score = 0d;
            var normalizedText = (string?)null;
            foreach (var token in Tokens)
            {
                if (token.Kind == SearchTokenKind.Phrase)
                {
                    normalizedText ??= NormalizeWhitespaceLower(text);
                    var phrase = NormalizeWhitespaceLower(token.Value);
                    var index = normalizedText.IndexOf(phrase, StringComparison.Ordinal);
                    if (index < 0) return new MatchResult(false, 0);
                    score += index * 0.1;
                    continue;
                }

                var fuzzy = TuiFuzzyMatcher.Filter([text], token.Value, candidate => candidate).Count > 0;
                if (!fuzzy) return new MatchResult(false, 0);
            }

            return new MatchResult(true, score);
        }

        private static IReadOnlyList<SearchToken> ParseTokens(string query)
        {
            var tokens = new List<SearchToken>();
            var buffer = new List<char>();
            var inQuote = false;
            var hadUnclosedQuote = false;

            void Flush(SearchTokenKind kind)
            {
                var value = new string(buffer.ToArray()).Trim();
                buffer.Clear();
                if (value.Length > 0) tokens.Add(new SearchToken(kind, value));
            }

            foreach (var ch in query)
            {
                if (ch == '"')
                {
                    if (inQuote)
                    {
                        Flush(SearchTokenKind.Phrase);
                        inQuote = false;
                    }
                    else
                    {
                        Flush(SearchTokenKind.Fuzzy);
                        inQuote = true;
                    }
                    continue;
                }

                if (!inQuote && char.IsWhiteSpace(ch))
                {
                    Flush(SearchTokenKind.Fuzzy);
                    continue;
                }

                buffer.Add(ch);
            }

            if (inQuote) hadUnclosedQuote = true;
            if (hadUnclosedQuote)
            {
                return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(token => new SearchToken(SearchTokenKind.Fuzzy, token))
                    .ToArray();
            }

            Flush(SearchTokenKind.Fuzzy);
            return tokens;
        }

        private static string NormalizeWhitespaceLower(string text)
            => Regex.Replace(text.ToLowerInvariant(), "\\s+", " ").Trim();
    }

    private enum SearchMode
    {
        Tokens,
        Regex
    }

    private enum SearchTokenKind
    {
        Fuzzy,
        Phrase
    }

    private sealed record SearchToken(SearchTokenKind Kind, string Value);
    private readonly record struct MatchResult(bool Matches, double Score);
}
