using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class SearchProviderRegistryTests
{
    private sealed class StubProvider(string id) : ISearchProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new SearchResponse(id, []));
    }

    [Fact]
    public void Register_ThenTryGetReturnsProvider()
    {
        var registry = new SearchProviderRegistry();
        registry.Register(new StubProvider("serper"));

        var provider = registry.TryGet("serper");
        Assert.NotNull(provider);
        Assert.Equal("serper", provider!.Id);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        var registry = new SearchProviderRegistry();
        registry.Register(new StubProvider("google-cse"));

        Assert.NotNull(registry.TryGet("Google-CSE"));
    }

    [Fact]
    public void TryGet_UnknownReturnsNull()
    {
        var registry = new SearchProviderRegistry();
        Assert.Null(registry.TryGet("brave"));
    }

    [Fact]
    public void Register_DuplicateIdThrows()
    {
        var registry = new SearchProviderRegistry();
        registry.Register(new StubProvider("serper"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new StubProvider("serper")));
    }

    [Fact]
    public void Register_DuplicateIdWithOverrideReplaces()
    {
        var registry = new SearchProviderRegistry();
        registry.Register(new StubProvider("serper"));
        registry.Register(new StubProvider("serper"), overrideExisting: true);

        Assert.Single(registry.Providers);
    }

    [Fact]
    public void Unregister_RemovesProviderAndReturnsBoolean()
    {
        var registry = new SearchProviderRegistry();
        registry.Register(new StubProvider("serper"));

        Assert.True(registry.Unregister("serper"));
        Assert.Null(registry.TryGet("serper"));
        Assert.False(registry.Unregister("serper"));
    }

    [Fact]
    public void Providers_ListsAllRegistered()
    {
        var registry = new SearchProviderRegistry();
        registry.Register(new StubProvider("serper"));
        registry.Register(new StubProvider("brave"));

        Assert.Equal(2, registry.Providers.Count);
    }

    [Fact]
    public void Register_EmptyIdThrows()
    {
        var registry = new SearchProviderRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new StubProvider("")));
    }
}
