using Xunit;
using PiSharp.Extensions;
using System.Net;
using PiSharp.Research.Search.GoogleCse;

namespace PiSharp.Research.Tests;

public sealed class GoogleCseSearchProviderTests
{
    private static SearchRequest Request(string query = "test query", string? key = "test-key", string? cx = "0123456789")
        => new(query, 5, key, cx is null ? null : new Dictionary<string, string?> { ["cx"] = cx });

    [Fact]
    public async Task GetsEndpointWithKeyCxQueryAndNum()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"items":[]}""");
        using var provider = new GoogleCseSearchProvider(handler);

        await provider.SearchAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Contains("https://www.googleapis.com/customsearch/v1", sent.RequestUri!.ToString());
        var query = sent.RequestUri!.Query;
        Assert.Contains("key=test-key", query);
        Assert.Contains("cx=0123456789", query);
        Assert.Contains("q=test%20query", query);
        Assert.Contains("num=5", query);
    }

    [Fact]
    public async Task MapsItems()
    {
        var json = """
            {
              "searchInformation": { "totalResults": "1200" },
              "items": [
                { "title": "Result One", "link": "https://example.com/1", "snippet": "First snippet." },
                { "title": "Result Two", "link": "https://example.com/2", "snippet": "Second snippet." }
              ]
            }
            """;
        using var provider = new GoogleCseSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var response = await provider.SearchAsync(Request(), CancellationToken.None);

        Assert.Equal("google-cse", response.Provider);
        Assert.Equal(1200, response.TotalResults);
        Assert.Collection(
            response.Results,
            item =>
            {
                Assert.Equal("Result One", item.Title);
                Assert.Equal("https://example.com/1", item.Url);
                Assert.Equal("First snippet.", item.Snippet);
            },
            item => Assert.Equal("Result Two", item.Title));
    }

    [Fact]
    public async Task ThrowsClearErrorWhenCxMissing()
    {
        using var provider = new GoogleCseSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"items":[]}"""));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Request(cx: null), CancellationToken.None));

        Assert.Contains("cx", exception.Message);
        Assert.Contains("extensions.pisharp-research.search.providers.google-cse.cx", exception.Message);
    }

    [Fact]
    public async Task ThrowsHttpErrorWithBodyExcerpt()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Forbidden, """{"error":{"message":"daily limit exceeded"}}""");
        using var provider = new GoogleCseSearchProvider(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync(Request(), CancellationToken.None));

        Assert.Contains("403", exception.Message);
        Assert.Contains("daily limit", exception.Message);
    }

    [Fact]
    public async Task ThrowsOnMissingApiKey()
    {
        using var provider = new GoogleCseSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"items":[]}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Request(key: null), CancellationToken.None));
    }

    [Fact]
    public async Task MapsEmptyResultsWhenItemsMissing()
    {
        using var provider = new GoogleCseSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"queries":{}}"""));

        var response = await provider.SearchAsync(Request(), CancellationToken.None);

        Assert.Empty(response.Results);
    }
}
