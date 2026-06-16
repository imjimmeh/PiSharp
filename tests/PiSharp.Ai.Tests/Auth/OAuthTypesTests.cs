using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class OAuthTypesTests
{
    [Fact]
    public void OAuthCredentialsStoresExpectedFields()
    {
        var extra = new Dictionary<string, object?> { ["accountId"] = "abc" };
        var creds = new OAuthCredentials("refresh123", "access456", 1717440000000, extra);

        Assert.Equal("refresh123", creds.Refresh);
        Assert.Equal("access456", creds.Access);
        Assert.Equal(1717440000000, creds.Expires);
        Assert.Equal("abc", creds.Extra?["accountId"]);
    }

    [Fact]
    public void OAuthAuthInfoStoresUrlAndInstructions()
    {
        var info = new OAuthAuthInfo("https://example.com/auth", "Complete login in browser");
        Assert.Equal("https://example.com/auth", info.Url);
        Assert.Equal("Complete login in browser", info.Instructions);
    }

    [Fact]
    public void OAuthPromptStoresFields()
    {
        var prompt = new OAuthPrompt("Enter API key:", Placeholder: "sk-...", AllowEmpty: false);
        Assert.Equal("Enter API key:", prompt.Message);
        Assert.Equal("sk-...", prompt.Placeholder);
        Assert.False(prompt.AllowEmpty);
    }
}
