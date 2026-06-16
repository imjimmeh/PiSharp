using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Models;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class ProviderCredentialResolverTests : IDisposable
{
    private static readonly string[] Keys = ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "ANTHROPIC_OAUTH_TOKEN", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_LOCATION", "GCLOUD_PROJECT", "PROXY_API_KEY"];

    public ProviderCredentialResolverTests()
    {
        ClearEnv();
        ModelRegistry.ResetToBuiltIns();
    }

    public void Dispose()
    {
        ClearEnv();
        ModelRegistry.ResetToBuiltIns();
    }

    [Fact]
    public async Task ExplicitApiKeyBeatsEnv()
    {
        var envValue = Guid.NewGuid().ToString("N");
        var explicitValue = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", envValue);
        var resolver = new ProviderCredentialResolver();
        var model = new ModelDescriptor("openai", "gpt-4o", "openai-responses");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions(ApiKey: explicitValue));

        Assert.Equal(explicitValue, result.ApiKey);
        Assert.Equal($"Bearer {explicitValue}", result.Headers!["Authorization"]);
    }

    [Fact]
    public async Task EnvApiKeyIsUsedWhenNoExplicitKey()
    {
        var envValue = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", envValue);
        var resolver = new ProviderCredentialResolver();
        var model = new ModelDescriptor("openai", "gpt-4o", "openai-responses");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal(envValue, result.ApiKey);
        Assert.True(result.IsAuthenticated);
    }

    [Fact]
    public async Task HeadersMergeInPrecedenceOrder()
    {
        var resolver = new ProviderCredentialResolver(ambientHeaders: new Dictionary<string, string> { ["x-base"] = "base" });
        var model = new ModelDescriptor(
            "openai",
            "gpt-4o",
            "openai-responses",
            Headers: new Dictionary<string, string> { ["x-model"] = "model" });
        var options = new AgentStreamOptions(Headers: new Dictionary<string, string> { ["x-request"] = "request" });

        var result = await resolver.ResolveAsync(model, options);

        Assert.Equal("base", result.Headers!["x-base"]);
        Assert.Equal("model", result.Headers["x-model"]);
        Assert.Equal("request", result.Headers["x-request"]);
    }

    [Fact]
    public async Task ProviderConfigApiKeyAndHeadersAreUsedForCustomProvider()
    {
        var envValue = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("PROXY_API_KEY", envValue);
        ModelRegistry.RegisterProviderConfig(new ModelProviderConfig(
            "custom-proxy",
            ApiKey: "PROXY_API_KEY",
            Headers: new Dictionary<string, string> { ["x-proxy"] = "1" }),
            "test-source");
        var resolver = new ProviderCredentialResolver();
        var model = new ModelDescriptor("custom-proxy", "model-1", "openai-responses");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal(envValue, result.ApiKey);
        Assert.Equal($"Bearer {envValue}", result.Headers!["Authorization"]);
        Assert.Equal("1", result.Headers["x-proxy"]);
    }

    [Fact]
    public async Task OAuthTokenUsedWhenApiKeyMissing()
    {
        var token = Guid.NewGuid().ToString("N");
        var storage = new InMemoryOAuthStorage();
        await storage.SetTokenAsync("anthropic", token);
        var resolver = new ProviderCredentialResolver(storage);
        var model = new ModelDescriptor("anthropic", "claude-sonnet-4-5", "anthropic-messages");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal(token, result.BearerToken);
        Assert.Equal($"Bearer {token}", result.Headers!["Authorization"]);
    }

    [Fact]
    public async Task FileOAuthStorageReadsExactProviderFromJsPiAuthShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "providers": {
            "minimax": { "apiKey": "minimax-key" }
          }
        }
        """);
        try
        {
            var storage = new FileOAuthStorage(path);
            var resolver = new ProviderCredentialResolver(storage);
            var model = new ModelDescriptor("minimax", "abab6.5s-chat", "openai-completions");

            var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

            Assert.Equal("minimax-key", result.BearerToken);
            Assert.Equal("Bearer minimax-key", result.Headers!["Authorization"]);
            Assert.True(result.IsAuthenticated);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FileOAuthStorageReadsOpenAiCodexAccessTokenFromJsPiAuthShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "openai-codex": {
            "type": "oauth",
            "access": "codex-access-token",
            "refresh": "codex-refresh-token",
            "expires": 9999999999999,
            "accountId": "acct_123"
          }
        }
        """);
        try
        {
            var storage = new FileOAuthStorage(path);
            var resolver = new ProviderCredentialResolver(storage);
            var model = new ModelDescriptor("openai-codex", "gpt-5.5", "openai-codex-responses");

            var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

            Assert.Equal("codex-access-token", result.BearerToken);
            Assert.Equal("Bearer codex-access-token", result.Headers!["Authorization"]);
            Assert.True(result.IsAuthenticated);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FileOAuthStorageReadsApiKeyCredentialKeyFromJsPiAuthShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "zai": {
            "type": "api_key",
            "key": "zai-key"
          }
        }
        """);
        try
        {
            var storage = new FileOAuthStorage(path);
            var resolver = new ProviderCredentialResolver(storage);
            var model = new ModelDescriptor("zai", "glm-4.5", "openai-completions");

            var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

            Assert.Equal("zai-key", result.BearerToken);
            Assert.Equal("Bearer zai-key", result.Headers!["Authorization"]);
            Assert.True(result.IsAuthenticated);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OAuthLookupFallsBackToBuiltInProviderAlias()
    {
        var storage = new InMemoryOAuthStorage();
        await storage.SetTokenAsync("openai", "openai-oauth");
        var resolver = new ProviderCredentialResolver(storage);
        var model = new ModelDescriptor("openai-codex", "codex", "openai-codex-responses");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal("openai-oauth", result.BearerToken);
        Assert.Equal("Bearer openai-oauth", result.Headers!["Authorization"]);
    }

    [Fact]
    public async Task OAuthLookupPrefersExactProviderBeforeAlias()
    {
        var storage = new InMemoryOAuthStorage();
        await storage.SetTokenAsync("openai", "openai-oauth");
        await storage.SetTokenAsync("openai-codex", "codex-oauth");
        var resolver = new ProviderCredentialResolver(storage);
        var model = new ModelDescriptor("openai-codex", "codex", "openai-codex-responses");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal("codex-oauth", result.BearerToken);
    }

    [Fact]
    public void EnvApiKeyDetectorMapsKnownProvider()
    {
        var envValue = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", envValue);

        Assert.Equal(envValue, EnvApiKeyDetector.GetEnvApiKey("anthropic"));
    }

    [Fact]
    public async Task AmbientCredentialsUseAuthenticatedMarkerWithoutBearerHeader()
    {
        using var tempFile = new TempFile();
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", tempFile.Path);
        Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", "project-123");
        Environment.SetEnvironmentVariable("GOOGLE_CLOUD_LOCATION", "us-central1");
        var resolver = new ProviderCredentialResolver();
        var model = new ModelDescriptor("google-vertex", "gemini-2.5-pro", "google-vertex");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal(EnvApiKeyDetector.AuthenticatedMarker, EnvApiKeyDetector.GetEnvApiKey("google-vertex"));
        Assert.True(result.IsAuthenticated);
        Assert.Null(result.ApiKey);
        Assert.False(result.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task OAuthStoredTokenBeatsEnvVar()
    {
        var oauthToken = Guid.NewGuid().ToString("N");
        var envValue = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", envValue);
        var storage = new InMemoryOAuthStorage();
        await storage.SetTokenAsync("openai", oauthToken);
        var resolver = new ProviderCredentialResolver(storage);
        var model = new ModelDescriptor("openai", "gpt-4o", "openai-responses");

        var result = await resolver.ResolveAsync(model, new AgentStreamOptions());

        Assert.Equal(oauthToken, result.BearerToken);
        Assert.Equal($"Bearer {oauthToken}", result.Headers!["Authorization"]);
        Assert.Null(result.ApiKey);
        Assert.True(result.IsAuthenticated);
    }

    [Fact]
    public void HasAmbientCredentialsDetectsBedrock()
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", Guid.NewGuid().ToString("N"));

        Assert.True(EnvApiKeyDetector.HasAmbientCredentials("amazon-bedrock"));
        Assert.Equal(EnvApiKeyDetector.AuthenticatedMarker, EnvApiKeyDetector.GetEnvApiKey("amazon-bedrock"));
    }

    private static void ClearEnv()
    {
        foreach (var key in Keys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();
        public void Dispose() => File.Delete(Path);
    }
}
