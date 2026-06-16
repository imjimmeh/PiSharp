using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class OpenAICodexOAuthProviderTests
{
    [Fact]
    public void ProviderExposesExpectedMetadata()
    {
        var provider = new OpenAICodexOAuthProvider();

        Assert.Equal("openai-codex", provider.Id);
        Assert.Contains("ChatGPT", provider.Name);
        Assert.True(provider.UsesCallbackServer);
    }

    [Fact]
    public void GetApiKeyReturnsAccessToken()
    {
        var provider = new OpenAICodexOAuthProvider();
        var key = provider.GetApiKey(new OAuthCredentials("r", "openaiapi-123", 0));

        Assert.Equal("openaiapi-123", key);
    }

    [Fact]
    public void ParseAuthorizationInputParsesUrl()
    {
        var (code, state) = OpenAICodexOAuthProvider.ParseAuthorizationInput(
            "https://localhost:1455/auth/callback?code=abc123&state=xyz789");

        Assert.Equal("abc123", code);
        Assert.Equal("xyz789", state);
    }

    [Fact]
    public void ParseAuthorizationInputParsesHashFormat()
    {
        var (code, state) = OpenAICodexOAuthProvider.ParseAuthorizationInput("def456#uvw123");

        Assert.Equal("def456", code);
        Assert.Equal("uvw123", state);
    }

    [Fact]
    public void ParseAuthorizationInputParsesQueryStringFormat()
    {
        var (code, state) = OpenAICodexOAuthProvider.ParseAuthorizationInput("code=ghi789&state=rst001");

        Assert.Equal("ghi789", code);
        Assert.Equal("rst001", state);
    }

    [Fact]
    public void ParseAuthorizationInputReturnsNullForEmpty()
    {
        var (code, state) = OpenAICodexOAuthProvider.ParseAuthorizationInput("   ");

        Assert.Null(code);
        Assert.Null(state);
    }

    [Fact]
    public void CreateStateGeneratesUniqueValues()
    {
        var state1 = OpenAICodexOAuthProvider.CreateState();
        var state2 = OpenAICodexOAuthProvider.CreateState();

        Assert.NotEmpty(state1);
        Assert.NotEmpty(state2);
        Assert.NotEqual(state1, state2);
        Assert.Equal(32, state1.Length);
    }

    [Fact]
    public async Task WaitForAuthorizationInputDoesNotPromptForManualCodeWhenCallbackArrives()
    {
        var manualInputCalled = false;
        var promptCalled = false;
        var callbacks = new OAuthLoginCallbacks(
            _ => Task.CompletedTask,
            (_, _) =>
            {
                promptCalled = true;
                return Task.FromResult("prompt-code");
            },
            OnManualCodeInput: _ =>
            {
                manualInputCalled = true;
                return Task.FromResult("manual-code");
            });

        var result = await OpenAICodexOAuthProvider.WaitForAuthorizationInputAsync(
            _ => Task.FromResult<(string Code, string State)?>(("callback-code", "state-123")),
            callbacks,
            "state-123",
            CancellationToken.None);

        Assert.Equal("callback-code", result.Code);
        Assert.Equal("state-123", result.State);
        Assert.False(manualInputCalled);
        Assert.False(promptCalled);
    }
}
