using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class GitHubCopilotOAuthProviderTests
{
    [Fact]
    public void ProviderExposesExpectedMetadata()
    {
        var provider = new GitHubCopilotOAuthProvider();

        Assert.Equal("github-copilot", provider.Id);
        Assert.Contains("GitHub Copilot", provider.Name);
        Assert.False(provider.UsesCallbackServer);
    }

    [Fact]
    public void GetApiKeyReturnsAccessToken()
    {
        var provider = new GitHubCopilotOAuthProvider();
        var key = provider.GetApiKey(new OAuthCredentials("r", "ghu_abc123", 0));

        Assert.Equal("ghu_abc123", key);
    }

    [Fact]
    public void NormalizeDomainExtractsHostname()
    {
        Assert.Equal("github.com", GitHubCopilotOAuthProvider.NormalizeDomain("github.com"));
        Assert.Equal("github.com", GitHubCopilotOAuthProvider.NormalizeDomain("https://github.com"));
        Assert.Equal("company.ghe.com", GitHubCopilotOAuthProvider.NormalizeDomain("company.ghe.com"));
        Assert.Equal("ent.example.com", GitHubCopilotOAuthProvider.NormalizeDomain("https://ent.example.com/path"));
        Assert.Null(GitHubCopilotOAuthProvider.NormalizeDomain(""));
        Assert.Null(GitHubCopilotOAuthProvider.NormalizeDomain("   "));
    }

    [Fact]
    public void GetBaseUrlFromTokenExtractsApiHost()
    {
        var token = "tid=abc;exp=12345;proxy-ep=proxy.individual.githubcopilot.com;sku=xyz";

        var url = GitHubCopilotOAuthProvider.GetBaseUrlFromToken(token);

        Assert.Equal("https://api.individual.githubcopilot.com", url);
    }

    [Fact]
    public void GetBaseUrlFromTokenReturnsNullWhenNoProxyEp()
    {
        var token = "tid=abc;exp=12345;sku=xyz";

        var url = GitHubCopilotOAuthProvider.GetBaseUrlFromToken(token);

        Assert.Null(url);
    }

    [Fact]
    public void GetBaseUrlHandlesEnterpriseDomain()
    {
        var result = GitHubCopilotOAuthProvider.GetBaseUrl(enterpriseDomain: "company.ghe.com");

        Assert.Equal("https://copilot-api.company.ghe.com", result);
    }

    [Fact]
    public void GetBaseUrlReturnsDefaultWhenNoTokenOrEnterprise()
    {
        var result = GitHubCopilotOAuthProvider.GetBaseUrl();

        Assert.Equal("https://api.individual.githubcopilot.com", result);
    }
}
