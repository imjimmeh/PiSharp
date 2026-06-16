using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class OAuthProviderRegistryTests
{
    private sealed class TestProvider : IOAuthProvider
    {
        public string Id { get; }
        public string Name { get; }
        public bool UsesCallbackServer => false;
        public TestProvider(string id, string name) { Id = id; Name = name; }
        public Task<OAuthCredentials> LoginAsync(OAuthLoginCallbacks callbacks, CancellationToken token)
            => Task.FromResult(new OAuthCredentials("r", "a", 0));
        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials c, CancellationToken t) => Task.FromResult(c);
        public string GetApiKey(OAuthCredentials c) => c.Access;
    }

    [Fact]
    public void RegisterAndRetrieveProvider()
    {
        var provider = new TestProvider("test", "Test");
        OAuthProviderRegistry.Register(provider);
        var retrieved = OAuthProviderRegistry.Get("test");
        Assert.NotNull(retrieved);
        Assert.Equal("Test", retrieved!.Name);
    }

    [Fact]
    public void GetUnknownProviderReturnsNull()
    {
        var result = OAuthProviderRegistry.Get("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void UnregisterRemovesProvider()
    {
        var provider = new TestProvider("temp", "Temp");
        OAuthProviderRegistry.Register(provider);
        Assert.NotNull(OAuthProviderRegistry.Get("temp"));
        OAuthProviderRegistry.Unregister("temp");
        Assert.Null(OAuthProviderRegistry.Get("temp"));
    }

    [Fact]
    public void GetAllReturnsRegisteredProviders()
    {
        var provider = new TestProvider("reg-test", "Registered");
        OAuthProviderRegistry.Register(provider);
        var providers = OAuthProviderRegistry.GetAll();
        Assert.NotEmpty(providers);
        Assert.Contains(providers, p => p.Id == "reg-test");
    }

    [Fact]
    public void IsOAuthProviderReturnsTrueForRegistered()
    {
        var provider = new TestProvider("oauth-check", "Check");
        OAuthProviderRegistry.Register(provider);
        Assert.True(OAuthProviderRegistry.IsOAuthProvider("oauth-check"));
        Assert.False(OAuthProviderRegistry.IsOAuthProvider("not-registered"));
    }
}
