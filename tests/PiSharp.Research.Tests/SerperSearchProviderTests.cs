using Xunit;
using PiSharp.Extensions;
using System.Net;
using PiSharp.Research.Search.Serper;

namespace PiSharp.Research.Tests;

public sealed class SerperSearchProviderTests
{
    private static SearchRequest Request(string query = "test query", string? key = "test-key", int maxResults = 5)
        => new(query, maxResults, key);

    [Fact]
    public async Task PostsToSerperEndpointWithApiKeyHeaderAndJsonBody()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"organic":[]}""");
        using var provider = new SerperSearchProvider(handler);

        await provider.SearchAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://google.serper.dev/search", sent.RequestUri!.ToString());
        Assert.Equal("test-key", sent.Headers.GetValues("X-API-KEY").Single());
        var body = await sent.Content!.ReadAsStringAsync();
        Assert.Contains("\"q\":\"test query\"", body);
        Assert.Contains("\"num\":5", body);
    }

    [Fact]
    public async Task MapsOrganicResults()
    {
        var json = """
            {
              "searchInformation": { "totalResults": "42" },
              "organic": [
                { "title": "Alpha Docs", "link": "https://alpha.dev/guide", "snippet": "The Alpha guide." },
                { "title": "Beta Blog", "link": "https://beta.dev/post", "snippet": "A Beta post." }
              ]
            }
            """;
        using var provider = new SerperSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, json));

        var response = await provider.SearchAsync(Request(), CancellationToken.None);

        Assert.Equal("serper", response.Provider);
        Assert.Equal(42, response.TotalResults);
        Assert.Collection(
            response.Results,
            item =>
            {
                Assert.Equal("Alpha Docs", item.Title);
                Assert.Equal("https://alpha.dev/guide", item.Url);
                Assert.Equal("The Alpha guide.", item.Snippet);
            },
            item => Assert.Equal("Beta Blog", item.Title));
    }

    [Fact]
    public async Task MapsEmptyResultsWhenOrganicMissing()
    {
        using var provider = new SerperSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"answerBox":{}}"""));

        var response = await provider.SearchAsync(Request(), CancellationToken.None);

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task ThrowsHttpErrorWithStatusAndBodyExcerpt()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "rate limited, try later");
        using var provider = new SerperSearchProvider(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync(Request(), CancellationToken.None));

        Assert.Contains("429", exception.Message);
        Assert.Contains("rate limited", exception.Message);
    }

    [Fact]
    public async Task ThrowsOnMissingApiKey()
    {
        using var provider = new SerperSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"organic":[]}"""));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Request(key: null), CancellationToken.None));

        Assert.Contains("API key", exception.Message);
    }

    [Fact]
    public async Task ThrowsOnEmptyQuery()
    {
        using var provider = new SerperSearchProvider(new StubHttpMessageHandler(HttpStatusCode.OK, """{"organic":[]}"""));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SearchAsync(Request(query: "  "), CancellationToken.None));
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        using var provider = new SerperSearchProvider(StubHttpMessageHandler.Hanging());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(Request(), cts.Token));
    }
}
