using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Search;

public sealed record FormattedSearchResult(string Text, TruncationResult? Truncation);

public static class SearchResultFormatter
{
    public static FormattedSearchResult FormatFileResults(FileSearchResult result, int limit)
    {
        if (result.RawText is not null) return new FormattedSearchResult(result.RawText, result.Truncation);
        var body = result.Results.Count == 0 ? "No files found" : string.Join("\n", result.Results);
        var truncation = Truncation.TruncateHead(body);
        var text = truncation.Content;
        if (result.ResultLimitReached is not null) text += $"\n\n[Stopped after {limit} results. Narrow the pattern or raise limit to continue.]";
        return new FormattedSearchResult(text, truncation.Truncated ? truncation : null);
    }

    public static FormattedSearchResult FormatContentResults(ContentSearchResult result, int limit)
    {
        if (result.RawText is not null) return new FormattedSearchResult(result.RawText, result.Truncation);
        var body = result.Matches.Count == 0 ? "No matches found" : string.Join("\n", result.Matches.Select(match => $"{match.RelativePath}:{match.LineNumber}:{match.Text}"));
        var truncation = Truncation.TruncateHead(body);
        var text = truncation.Content;
        if (result.MatchLimitReached is not null) text += $"\n\n[Stopped after {limit} matches. Narrow the query or raise limit to continue.]";
        return new FormattedSearchResult(text, truncation.Truncated ? truncation : null);
    }
}
