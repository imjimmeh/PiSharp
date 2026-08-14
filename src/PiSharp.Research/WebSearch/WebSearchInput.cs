using System.ComponentModel;
using PiSharp.Extensions;

namespace PiSharp.Research.WebSearch;

/// <summary>
/// Arguments for the <c>web_search</c> tool. Provider selection, result count,
/// and timeouts default from <c>extensions.pisharp-research.*</c> settings and
/// can be overridden per call.
/// </summary>
public sealed record WebSearchInput(
    [property: Description("Search query text")] string Query,
    [property: Description("Maximum number of results to return (1-10). Defaults to the configured result count.")]
    int? MaxResults = null,
    [property: Description("Provider id to use (serper, google-cse, brave). Defaults to the configured provider.")]
    string? Provider = null);

/// <summary>
/// Structured details carried on the <c>web_search</c> tool result.
/// </summary>
public sealed record WebSearchDetails(
    string Provider,
    string Query,
    IReadOnlyList<SearchResultItem> Results,
    int? TotalResults = null,
    string? Warning = null);
