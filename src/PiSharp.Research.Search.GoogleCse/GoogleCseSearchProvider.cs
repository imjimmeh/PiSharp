using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.Research.Search.GoogleCse;

/// <summary>
/// <see cref="ISearchProvider"/> for Google Custom Search JSON API. Sends
/// <c>GET https://www.googleapis.com/customsearch/v1</c> with
/// <c>key</c>/<c>cx</c>/<c>q</c>/<c>num</c> query parameters and maps
/// <c>items[]</c> to normalized <see cref="SearchResultItem"/>s. The custom
/// search engine id (<c>cx</c>) arrives via <see cref="SearchRequest.Parameters"/>
/// (from settings <c>extensions.pisharp-research.search.providers.google-cse.cx</c>);
/// an empty <c>cx</c> yields a clear error naming the key.
/// </summary>
public sealed class GoogleCseSearchProvider : ISearchProvider, IDisposable
{
    internal const string Endpoint = "https://www.googleapis.com/customsearch/v1";
    private const int ErrorBodyExcerptLength = 300;

    private readonly HttpClient _client;

    public GoogleCseSearchProvider(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public string Id => "google-cse";

    public string DisplayName => "Google Custom Search";

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query must not be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException($"Search provider '{Id}' requires an API key.");

        var cx = request.Parameters is not null && request.Parameters.TryGetValue("cx", out var configuredCx) ? configuredCx : null;
        if (string.IsNullOrWhiteSpace(cx))
        {
            throw new InvalidOperationException(
                $"Search provider '{Id}' requires a Custom Search engine id: configure 'extensions.pisharp-research.search.providers.google-cse.cx'.");
        }

        var query = Uri.EscapeDataString(request.Query);
        var url = $"{Endpoint}?key={Uri.EscapeDataString(request.ApiKey)}&cx={Uri.EscapeDataString(cx)}&q={query}&num={request.MaxResults}";

        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {await ReadExcerptAsync(response, cancellationToken).ConfigureAwait(false)}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Map(body);
    }

    internal static SearchResponse Map(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var results = new List<SearchResultItem>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new SearchResultItem(
                    Title: item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    Url: item.TryGetProperty("link", out var link) ? link.GetString() ?? string.Empty : string.Empty,
                    Snippet: item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? string.Empty : string.Empty));
            }
        }

        int? totalResults = null;
        if (root.TryGetProperty("searchInformation", out var info)
            && info.TryGetProperty("totalResults", out var total)
            && int.TryParse(total.GetString(), out var parsedTotal))
        {
            totalResults = parsedTotal;
        }

        return new SearchResponse(Provider: "google-cse", Results: results, TotalResults: totalResults);
    }

    private static async Task<string> ReadExcerptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > ErrorBodyExcerptLength) body = body[..ErrorBodyExcerptLength] + "…";
            return string.IsNullOrWhiteSpace(body) ? "(empty response body)" : body;
        }
        catch (Exception)
        {
            return "(unreadable response body)";
        }
    }

    public void Dispose() => _client.Dispose();
}
