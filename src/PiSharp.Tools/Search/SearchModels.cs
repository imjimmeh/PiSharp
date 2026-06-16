using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Search;

public sealed record FileSearchRequest(string Root, string Pattern, int Limit);

public sealed record FileSearchResult(
    IReadOnlyList<string> Results,
    int? ResultLimitReached = null,
    TruncationResult? Truncation = null,
    string? RawText = null);

public sealed record ContentSearchRequest(
    string Root,
    string Pattern,
    string? Glob,
    bool IgnoreCase,
    bool Literal,
    int Context,
    int Limit);

public sealed record ContentSearchMatch(string RelativePath, int LineNumber, string Text);

public sealed record ContentSearchResult(
    IReadOnlyList<ContentSearchMatch> Matches,
    int? MatchLimitReached = null,
    bool LinesTruncated = false,
    TruncationResult? Truncation = null,
    string? RawText = null);
