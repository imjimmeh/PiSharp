using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class IOAuthProviderTests
{
    private sealed class TestProvider : IOAuthProvider
    {
        public string Id => "test";
        public string Name => "Test Provider";
        public bool UsesCallbackServer => false;

        public Task<OAuthCredentials> LoginAsync(OAuthLoginCallbacks callbacks, CancellationToken token)
            => Task.FromResult(new OAuthCredentials("refresh", "access", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3600000));

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken token)
            => Task.FromResult(credentials);

        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }

    [Fact]
    public void ProviderExposesIdNameAndFlags()
    {
        var provider = new TestProvider();
        Assert.Equal("test", provider.Id);
        Assert.Equal("Test Provider", provider.Name);
        Assert.False(provider.UsesCallbackServer);
    }

    [Fact]
    public async Task LoginAsyncReturnsCredentials()
    {
        var provider = new TestProvider();
        var callbacks = new OAuthLoginCallbacks(
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult("test"));
        var creds = await provider.LoginAsync(callbacks, CancellationToken.None);
        Assert.Equal("refresh", creds.Refresh);
        Assert.Equal("access", creds.Access);
    }

    [Fact]
    public void GetApiKeyReturnsAccessToken()
    {
        var provider = new TestProvider();
        var creds = new OAuthCredentials("r", "key123", 0);
        Assert.Equal("key123", provider.GetApiKey(creds));
    }
}
