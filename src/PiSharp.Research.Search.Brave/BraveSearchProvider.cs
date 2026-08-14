using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.Research.Search.Brave;

/// <summary>
/// <see cref="ISearchProvider"/> for the Brave Search Web Search API. Sends
/// <c>GET https://api.search.brave.com/res/v1/web/search</c> with an
/// <c>X-Subscription-Token</c> header and maps <c>web.results[]</c> to
/// normalized <see cref="SearchResultItem"/>s.
/// </summary>
public sealed class BraveSearchProvider : ISearchProvider, IDisposable
{
    internal const string Endpoint = "https://api.search.brave.com/res/v1/web/search";
    private const int ErrorBodyExcerptLength = 300;

    private readonly HttpClient _client;

    public BraveSearchProvider(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public string Id => "brave";

    public string DisplayName => "Brave Search";

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query must not be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException($"Search provider '{Id}' requires an API key.");

        var query = Uri.EscapeDataString(request.Query);
        var url = $"{Endpoint}?q={query}&count={request.MaxResults}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.TryAddWithoutValidation("X-Subscription-Token", request.ApiKey);

        using var response = await _client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
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
        if (root.TryGetProperty("web", out var web)
            && web.ValueKind == JsonValueKind.Object
            && web.TryGetProperty("results", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new SearchResultItem(
                    Title: item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    Url: item.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                    Snippet: item.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty));
            }
        }

        return new SearchResponse(Provider: "brave", Results: results, TotalResults: null);
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
