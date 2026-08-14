using Xunit;
using PiSharp.Extensions;
using PiSharp.Research.WebSearch;

namespace PiSharp.Research.Tests;

public sealed class SearchCoordinatorTests
{
    private static SearchCoordinator Coordinator(Dictionary<string, object?>? settings = null)
        => new(settings is null ? _ => null : key => settings.TryGetValue(key, out var value) ? value : null);

    [Fact]
    public void DefaultsToConfiguredProviderAndClampedResultCount()
    {
        var coordinator = Coordinator(new Dictionary<string, object?>
        {
            ["search.resultCount"] = 3,
        });

        var plan = coordinator.ResolvePlan(providerOverride: null);

        Assert.Null(plan.Error);
        Assert.Equal("serper", plan.ProviderId);
        Assert.Equal(3, plan.MaxResults);
    }

    [Fact]
    public void PerCallProviderOverrideWins()
    {
        var coordinator = Coordinator(new Dictionary<string, object?>
        {
            ["search.provider"] = "serper",
        });

        var plan = coordinator.ResolvePlan("brave");

        Assert.Equal("brave", plan.ProviderId);
    }

    [Fact]
    public void DisabledSearchProducesClearError()
    {
        var coordinator = Coordinator(new Dictionary<string, object?>
        {
            ["search.enabled"] = false,
        });

        var plan = coordinator.ResolvePlan(providerOverride: null);

        Assert.Null(plan.ProviderId);
        Assert.NotNull(plan.Error);
        Assert.Contains("web_search is disabled", plan.Error);
        Assert.Contains("extensions.pisharp-research.search.enabled", plan.Error);
    }

    [Fact]
    public void DisabledProviderProducesClearError()
    {
        var coordinator = Coordinator(new Dictionary<string, object?>
        {
            ["search.providers.brave.enabled"] = false,
        });

        var plan = coordinator.ResolvePlan("brave");

        Assert.Null(plan.ProviderId);
        Assert.NotNull(plan.Error);
        Assert.Contains("'brave' is disabled", plan.Error);
        Assert.Contains("extensions.pisharp-research.search.providers.brave.enabled", plan.Error);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(100, 10)]
    public void ClampsResultCountToToolWindow(int requested, int expected)
    {
        Assert.Equal(expected, SearchCoordinator.ClampMaxResults(requested));
    }

    [Theory]
    [InlineData(11, 10)]
    [InlineData(0, 1)]
    public void PlanClampsConfiguredResultCount(int configured, int expected)
    {
        var coordinator = Coordinator(new Dictionary<string, object?>
        {
            ["search.resultCount"] = configured,
        });

        var plan = coordinator.ResolvePlan(providerOverride: null);

        Assert.Equal(expected, plan.MaxResults);
    }

    [Fact]
    public void MissingKeyErrorNamesEnvVarAndSettingsKey()
    {
        var message = SearchCoordinator.DescribeMissingKey("serper", configuredKey: null);

        Assert.Contains("SERPER_API_KEY", message); // provider env var convention is named generically
        Assert.Contains("extensions.pisharp-research.search.providers.serper.apiKey", message);
    }

    [Fact]
    public void FormatResultsProducesNumberedList()
    {
        var response = new SearchResponse(
            "serper",
            [
                new SearchResultItem("First Result", "https://example.com/1", "First snippet."),
                new SearchResultItem("Second Result", "https://example.com/2", "Second snippet."),
            ],
            TotalResults: 1200);

        var text = SearchCoordinator.FormatResults(response);

        Assert.Contains("2 result(s) of 1200", text);
        Assert.Contains("1. First Result", text);
        Assert.Contains("   https://example.com/1", text);
        Assert.Contains("   First snippet.", text);
        Assert.Contains("2. Second Result", text);
    }

    [Fact]
    public void FormatResultsHandlesEmptyResults()
    {
        var response = new SearchResponse("serper", []);

        var text = SearchCoordinator.FormatResults(response);

        Assert.Contains("no results", text);
    }

    [Fact]
    public void FormatResultsIncludesWarning()
    {
        var response = new SearchResponse("serper", [], Warning: "results truncated");

        var text = SearchCoordinator.FormatResults(response);

        Assert.Contains("results truncated", text);
    }
}
