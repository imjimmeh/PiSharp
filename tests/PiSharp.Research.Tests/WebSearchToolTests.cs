using Xunit;
using System.Net;
using System.Text.Json;
using PiSharp.Ai.Auth;
using PiSharp.Research.Search.Serper;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Abstractions.Messages;
using PiSharp.Research.WebSearch;

namespace PiSharp.Research.Tests;

public sealed class WebSearchToolTests
{
    private const string SerperJson = """
        {
          "searchInformation": { "totalResults": "1200" },
          "organic": [
            { "title": "Alpha Docs", "link": "https://alpha.dev/guide", "snippet": "The Alpha guide." },
            { "title": "Beta Blog", "link": "https://beta.dev/post", "snippet": "A Beta post." }
          ]
        }
        """;

    private sealed class StubCredentialResolver(string? apiKey) : IServiceCredentialResolver
    {
        public string? ResolvedServiceId { get; private set; }
        public ServiceCredentialOptions? ResolvedOptions { get; private set; }

        public Task<ProviderCredentialResult> ResolveAsync(
            string serviceId,
            ServiceCredentialOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ResolvedServiceId = serviceId;
            ResolvedOptions = options;
            return Task.FromResult(new ProviderCredentialResult(
                ApiKey: apiKey,
                IsAuthenticated: apiKey is not null));
        }
    }

    private static WebSearchTool BuildTool(
        HttpMessageHandler handler,
        Dictionary<string, object?>? settings = null,
        string? credentialKey = "test-key",
        string? providerOverride = null)
    {
        var provider = new SerperSearchProvider(handler);
        var lookup = new Dictionary<string, ISearchProvider> { ["serper"] = provider };
        return new WebSearchTool(
            providerLookup: id => lookup.TryGetValue(id, out var p) ? p : null,
            providerList: () => lookup.Values.ToArray(),
            settings: key => settings is not null && settings.TryGetValue(key, out var value) ? value : null,
            credentials: new StubCredentialResolver(credentialKey));
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task RunsEndToEndAgainstStubHandler()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var tool = BuildTool(handler);

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "attention is all you need"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("web_search (serper): 2 result(s) of 1200", text);
        Assert.Contains("1. Alpha Docs", text);
        Assert.Contains("   https://alpha.dev/guide", text);
        Assert.Contains("   The Alpha guide.", text);
        Assert.Contains("2. Beta Blog", text);

        var details = Assert.IsType<WebSearchDetails>(result.Details);
        Assert.Equal("serper", details.Provider);
        Assert.Equal("attention is all you need", details.Query);
        Assert.Equal(2, details.Results.Count);
        Assert.Equal(1200, details.TotalResults);
    }

    [Fact]
    public async Task PerCallProviderOverrideWins()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var tool = BuildTool(handler);

        await tool.ExecuteAsync("call-1", Args("""{"query": "x", "provider": "serper"}"""), CancellationToken.None);

        Assert.Single(handler.Requests); // resolved to serper despite settings default
    }

    [Fact]
    public async Task MaxResultsClampedToToolWindow()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var settings = new Dictionary<string, object?> { ["search.resultCount"] = 100 };
        var tool = BuildTool(handler, settings);

        await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        var body = await handler.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("\"num\":10", body);
    }

    [Fact]
    public async Task PerCallMaxResultsOverridesSettings()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var settings = new Dictionary<string, object?> { ["search.resultCount"] = 3 };
        var tool = BuildTool(handler, settings);

        await tool.ExecuteAsync("call-1", Args("""{"query": "x", "maxResults": 1}"""), CancellationToken.None);

        var body = await handler.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("\"num\":1", body);
    }

    [Fact]
    public async Task DisabledSearchReturnsClearMessage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var settings = new Dictionary<string, object?> { ["search.enabled"] = false };
        var tool = BuildTool(handler, settings);

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("web_search is disabled", text);
        Assert.Contains("extensions.pisharp-research.search.enabled", text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DisabledProviderReturnsClearMessage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var settings = new Dictionary<string, object?> { ["search.providers.serper.enabled"] = false };
        var tool = BuildTool(handler, settings);

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("'serper' is disabled", text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnknownProviderReturnsClearMessage()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var tool = BuildTool(handler);

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x", "provider": "nope"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("Unknown search provider 'nope'", text);
        Assert.Contains("serper", text); // names the registered provider
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingKeyNamesEnvVarAndSettingsKey()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var tool = BuildTool(handler, credentialKey: null);

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("SERPER_API_KEY", text);
        Assert.Contains("extensions.pisharp-research.search.providers.serper.apiKey", text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResolvesCredentialWithConfiguredKeyAndNoAuthHeader()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var settings = new Dictionary<string, object?> { ["search.providers.serper.apiKey"] = "SERPER_API_KEY" };
        var tool = BuildTool(handler, settings);

        await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        // The configured key (env-var name form) is passed through to the resolver; providers
        // send keys in their own headers, so UseAuthHeader must be false.
        Assert.Single(handler.Requests);
        Assert.Equal("test-key", handler.Requests[0].Headers.GetValues("X-API-KEY").Single());
    }

    [Fact]
    public async Task TimeoutReturnsClearMessage()
    {
        var tool = BuildTool(
            StubHttpMessageHandler.Hanging(),
            new Dictionary<string, object?> { ["search.timeoutSeconds"] = 0.05 });

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("timed out", text);
        Assert.Contains("extensions.pisharp-research.search.timeoutSeconds", text);
    }

    [Fact]
    public async Task HttpErrorReturnsModelVisibleText()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.BadGateway, "upstream broke");
        var tool = BuildTool(handler);

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("HTTP error", text);
        Assert.Contains("502", text);
        Assert.Contains("upstream broke", text);
    }

    [Fact]
    public async Task MissingQueryReturnsArgumentError()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, SerperJson);
        var tool = BuildTool(handler);

        var result = await tool.ExecuteAsync("call-1", Args("""{}"""), CancellationToken.None);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("'query'", text);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PassesCxParameterForGoogleCse()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"items":[]}""");
        var settings = new Dictionary<string, object?>
        {
            ["search.provider"] = "google-cse",
            ["search.providers.google-cse.cx"] = "engine-1",
        };
        var provider = new PiSharp.Research.Search.GoogleCse.GoogleCseSearchProvider(handler);
        var lookup = new Dictionary<string, ISearchProvider> { ["google-cse"] = provider };
        var tool = new WebSearchTool(
            providerLookup: id => lookup.TryGetValue(id, out var p) ? p : null,
            providerList: () => lookup.Values.ToArray(),
            settings: key => settings.TryGetValue(key, out var value) ? value : null,
            credentials: new StubCredentialResolver("test-key"));

        var result = await tool.ExecuteAsync("call-1", Args("""{"query": "x"}"""), CancellationToken.None);

        Assert.Contains("cx=engine-1", handler.Requests.Single().RequestUri!.Query);
        Assert.DoesNotContain("timed out", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }
}
