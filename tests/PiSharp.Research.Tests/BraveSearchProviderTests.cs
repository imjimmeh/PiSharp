using Xunit;
using PiSharp.Extensions;
using System.Net;
using PiSharp.Research.Search.Brave;

namespace PiSharp.Research.Tests;

public sealed class BraveSearchProviderTests
{
    private static SearchRequest Request(string query = "test query", string? key = "test-key")
        => new(query, 5, key);

    [Fact]
    public async Task GetsEndpointWithSubscriptionTokenHeaderAndQueryParams()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"web":{"results":[]}}""");
        using var provider = new BraveSearchProvider(handler);

        await provider.SearchAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Contains("https://api.search.brave.com/res/v1/web/search", sent.RequestUri!.ToString());
        Assert.Equal("test-key", sent.Headers.GetValues("X-Subscription-Token").Single());
        Assert.Contains("q=test%20query", sent.RequestUri!.Query);
        Assert.Contains("count=5", sent.RequestUri!.Query);
    }

    [Fact]
    public async Task MapsWebResults()
    {
        var json = """
            {
              "web": {
                "results": [
                  { "title": "Brave Result", "url": "https://brave.example/1", "description": "Brave snippet." },
                  { "title": "Second", "url": "https://brave.example/2", "description": "Second snippet." }
                ]
              }
            }
            """;
        using var provider = new BraveSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var response = await provider.SearchAsync(Request(), CancellationToken.None);

        Assert.Equal("brave", response.Provider);
        Assert.Collection(
            response.Results,
            item =>
            {
                Assert.Equal("Brave Result", item.Title);
                Assert.Equal("https://brave.example/1", item.Url);
                Assert.Equal("Brave snippet.", item.Snippet);
            },
            item => Assert.Equal("Second", item.Title));
    }

    [Fact]
    public async Task MapsEmptyResultsWhenWebSectionMissing()
    {
        using var provider = new BraveSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"query":{"original":"x"}}"""));

        var response = await provider.SearchAsync(Request(), CancellationToken.None);

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task ThrowsHttpErrorWithBodyExcerpt()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "invalid token");
        using var provider = new BraveSearchProvider(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync(Request(), CancellationToken.None));

        Assert.Contains("401", exception.Message);
        Assert.Contains("invalid token", exception.Message);
    }

    [Fact]
    public async Task ThrowsOnMissingApiKey()
    {
        using var provider = new BraveSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"web":{"results":[]}}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Request(key: null), CancellationToken.None));
    }
}
