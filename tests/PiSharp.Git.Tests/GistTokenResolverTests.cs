using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Git.Tests;

public sealed class GistTokenResolverTests : IDisposable
{
    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly InMemoryOAuthStorage _storage = new();
    private readonly GitPluginOptions _options = new();

    private void SetEnv(string name, string? value)
    {
        _savedEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private async Task<GistTokenResolver.Resolution> ResolveAsync()
        => await new GistTokenResolver(_storage, _options).ResolveAsync();

    [Fact]
    public async Task EnvTokenWins()
    {
        SetEnv("GITHUB_TOKEN", "ghp_classic");
        var result = await ResolveAsync();
        Assert.True(result.Success);
        Assert.Equal("ghp_classic", result.Token);
    }

    [Fact]
    public async Task AuthStoreTokenIsUsed()
    {
        await _storage.SetTokenAsync("github", "ghp_from_store");
        var result = await ResolveAsync();
        Assert.True(result.Success);
        Assert.Equal("ghp_from_store", result.Token);
    }

    [Fact]
    public async Task OAuthAccessTokenIsUsed()
    {
        await _storage.SetOAuthCredentialsAsync("github", new OAuthCredentials("refresh", "gho_oauth", 3600));
        var result = await ResolveAsync();
        Assert.True(result.Success);
        Assert.Equal("gho_oauth", result.Token);
    }

    [Fact]
    public async Task FineGrainedPatIsRejected()
    {
        SetEnv("GITHUB_TOKEN", "github_pat_abc123");
        var result = await ResolveAsync();
        Assert.False(result.Success);
        Assert.Contains("fine-grained", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopilotJwtIsRejected()
    {
        SetEnv("GITHUB_TOKEN", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U");
        var result = await ResolveAsync();
        Assert.False(result.Success);
        Assert.Contains("Copilot", result.Error);
    }

    [Fact]
    public async Task MissingTokenReportsActionableError()
    {
        var result = await ResolveAsync();
        Assert.False(result.Success);
        Assert.Contains("GITHUB_TOKEN", result.Error);
        Assert.Contains("github", result.Error);
    }

    [Fact]
    public void UnusableTokenDetection()
    {
        Assert.NotNull(GistTokenResolver.IsUnusableToken("github_pat_x"));
        Assert.NotNull(GistTokenResolver.IsUnusableToken("eyJhbGciOiJIUzI1NiJ9.eyJhIjoiYiJ9.sig"));
        Assert.Null(GistTokenResolver.IsUnusableToken("ghp_classic"));
        Assert.Null(GistTokenResolver.IsUnusableToken(""));
    }
}
