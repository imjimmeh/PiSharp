namespace PiSharp.Extensions;

/// <summary>
/// A search backend (e.g. Serper, Google Custom Search, Brave) that returns
/// normalized web results for a query. Provider selection, credential
/// resolution, result-count clamping, and error text are handled by the
/// caller; providers are deliberately dumb and never touch settings.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable provider id, e.g. "serper" | "google-cse" | "brave".</summary>
    string Id { get; }

    /// <summary>Human-readable provider name, e.g. "Serper (Google)".</summary>
    string DisplayName { get; }

    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A normalized request to a search provider.</summary>
public sealed record SearchRequest(
    string Query,
    int MaxResults,
    string? ApiKey,
    IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>A single normalized web result.</summary>
public sealed record SearchResultItem(string Title, string Url, string Snippet);

/// <summary>The normalized response from a search provider.</summary>
public sealed record SearchResponse(
    string Provider,
    IReadOnlyList<SearchResultItem> Results,
    int? TotalResults = null,
    string? Warning = null);
