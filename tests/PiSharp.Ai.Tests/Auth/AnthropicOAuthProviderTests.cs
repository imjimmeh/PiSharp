using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class AnthropicOAuthProviderTests
{
    [Fact]
    public void ProviderExposesExpectedMetadata()
    {
        var provider = new AnthropicOAuthProvider();

        Assert.Equal("anthropic", provider.Id);
        Assert.Contains("Anthropic", provider.Name);
        Assert.True(provider.UsesCallbackServer);
    }

    [Fact]
    public void GetApiKeyReturnsAccessToken()
    {
        var provider = new AnthropicOAuthProvider();
        var key = provider.GetApiKey(new OAuthCredentials("r", "my-token", 0));

        Assert.Equal("my-token", key);
    }

    [Fact]
    public void ParseAuthorizationInputParsesUrlWithCodeAndState()
    {
        var (code, state) = AnthropicOAuthProvider.ParseAuthorizationInput(
            "https://localhost:53692/callback?code=abc123&state=xyz789");

        Assert.Equal("abc123", code);
        Assert.Equal("xyz789", state);
    }

    [Fact]
    public void ParseAuthorizationInputParsesHashFormat()
    {
        var (code, state) = AnthropicOAuthProvider.ParseAuthorizationInput("abc123#xyz789");

        Assert.Equal("abc123", code);
        Assert.Equal("xyz789", state);
    }

    [Fact]
    public void ParseAuthorizationInputParsesQueryStringFormat()
    {
        var (code, state) = AnthropicOAuthProvider.ParseAuthorizationInput("code=def456&state=uvw123");

        Assert.Equal("def456", code);
        Assert.Equal("uvw123", state);
    }

    [Fact]
    public void ParseAuthorizationInputReturnsPlainCode()
    {
        var (code, state) = AnthropicOAuthProvider.ParseAuthorizationInput("plain-code-123");

        Assert.Equal("plain-code-123", code);
        Assert.Null(state);
    }

    [Fact]
    public void ParseAuthorizationInputReturnsNullForEmptyInput()
    {
        var (code, state) = AnthropicOAuthProvider.ParseAuthorizationInput("   ");

        Assert.Null(code);
        Assert.Null(state);
    }
}
