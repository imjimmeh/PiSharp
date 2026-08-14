using System.Net.Http.Json;
using System.Text.Json;
using PiSharp.Ai.Auth;
using PiSharp.Extensions;

namespace PiSharp.Research.Search.Serper;

/// <summary>
/// <see cref="ISearchProvider"/> for Serper (Google SERP API). Sends
/// <c>POST https://google.serper.dev/search</c> with an <c>X-API-KEY</c>
/// header and maps <c>organic[]</c> results to normalized
/// <see cref="SearchResultItem"/>s. Deliberately dumb: credentials, selection,
/// and timeouts are the caller's job.
/// </summary>
public sealed class SerperSearchProvider : ISearchProvider, IDisposable
{
    internal const string Endpoint = "https://google.serper.dev/search";
    private const int ErrorBodyExcerptLength = 300;

    private readonly HttpClient _client;

    public SerperSearchProvider(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
    }

    public string Id => "serper";

    public string DisplayName => "Serper (Google)";

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query must not be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException($"Search provider '{Id}' requires an API key.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.TryAddWithoutValidation("X-API-KEY", request.ApiKey);
        httpRequest.Content = JsonContent.Create(new { q = request.Query, num = request.MaxResults });

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
        if (root.TryGetProperty("organic", out var organic) && organic.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in organic.EnumerateArray())
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

        return new SearchResponse(Provider: "serper", Results: results, TotalResults: totalResults);
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
