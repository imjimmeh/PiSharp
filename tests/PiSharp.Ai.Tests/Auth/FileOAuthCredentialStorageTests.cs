using System.Text.Json;
using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class FileOAuthCredentialStorageTests
{
    [Fact]
    public async Task RoundTripOAuthCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            var creds = new OAuthCredentials("refresh1", "access1", 1717440000000);

            await storage.SetOAuthCredentialsAsync("anthropic", creds);
            var retrieved = await storage.GetOAuthCredentialsAsync("anthropic");

            Assert.NotNull(retrieved);
            Assert.Equal("refresh1", retrieved!.Refresh);
            Assert.Equal("access1", retrieved.Access);
            Assert.Equal(1717440000000, retrieved.Expires);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SetOAuthCredentialsPersistsExtraFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            var extra = new Dictionary<string, object?> { ["accountId"] = "abc123" };
            var creds = new OAuthCredentials("r", "a", 0, extra);

            await storage.SetOAuthCredentialsAsync("openai", creds);
            var retrieved = await storage.GetOAuthCredentialsAsync("openai");

            Assert.NotNull(retrieved);
            Assert.Equal("abc123", retrieved!.Extra?["accountId"]?.ToString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ListStoredProvidersReturnsSetProviders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            await storage.SetOAuthCredentialsAsync("provider-a", new OAuthCredentials("r", "a", 0));
            await storage.SetOAuthCredentialsAsync("provider-b", new OAuthCredentials("r", "b", 0));

            var providers = await storage.ListStoredProvidersAsync();

            Assert.Contains("provider-a", providers);
            Assert.Contains("provider-b", providers);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveTokenAlsoRemovesOAuthCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            await storage.SetOAuthCredentialsAsync("test", new OAuthCredentials("r", "a", 0));
            Assert.NotNull(await storage.GetOAuthCredentialsAsync("test"));

            await storage.RemoveTokenAsync("test");
            Assert.Null(await storage.GetOAuthCredentialsAsync("test"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetOAuthCredentialsReturnsNullForUnknownProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            var result = await storage.GetOAuthCredentialsAsync("nonexistent");
            Assert.Null(result);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetOAuthCredentialsReturnsNullWhenOnlyTokenStored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        try
        {
            var storage = new FileOAuthStorage(path);
            await storage.SetTokenAsync("myprovider", "my-token-value");
            var result = await storage.GetOAuthCredentialsAsync("myprovider");
            Assert.Null(result);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTokenPrefersFreshNestedProviderCredentialsOverExpiredRootCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "openai-codex": {
            "type": "oauth",
            "access": "expired-root-access-token",
            "refresh": "expired-root-refresh-token",
            "expires": 1717754895504,
            "accountId": "acct_root"
          },
          "providers": {
            "openai-codex": {
              "access": "fresh-nested-access-token",
              "refresh": "fresh-nested-refresh-token",
              "expires": 1780831538243,
              "accountId": "acct_nested"
            }
          }
        }
        """);
        try
        {
            var storage = new FileOAuthStorage(path);

            var token = await storage.GetTokenAsync("openai-codex");

            Assert.Equal("fresh-nested-access-token", token);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetOAuthCredentialsPrefersNestedProviderCredentialsOverRootCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "openai-codex": {
            "type": "oauth",
            "access": "expired-root-access-token",
            "refresh": "expired-root-refresh-token",
            "expires": 1717754895504,
            "accountId": "acct_root"
          },
          "providers": {
            "openai-codex": {
              "access": "fresh-nested-access-token",
              "refresh": "fresh-nested-refresh-token",
              "expires": 1780831538243,
              "accountId": "acct_nested"
            }
          }
        }
        """);
        try
        {
            var storage = new FileOAuthStorage(path);

            var credentials = await storage.GetOAuthCredentialsAsync("openai-codex");

            Assert.NotNull(credentials);
            Assert.Equal("fresh-nested-access-token", credentials!.Access);
            Assert.Equal("fresh-nested-refresh-token", credentials.Refresh);
            Assert.Equal(1780831538243, credentials.Expires);
            Assert.Equal("acct_nested", credentials.Extra?["accountId"]?.ToString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SetOAuthCredentialsRemovesLegacyRootProviderCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-auth-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "openai-codex": {
            "type": "oauth",
            "access": "expired-root-access-token",
            "refresh": "expired-root-refresh-token",
            "expires": 1717754895504
          }
        }
        """);
        try
        {
            var storage = new FileOAuthStorage(path);

            await storage.SetOAuthCredentialsAsync(
                "openai-codex",
                new OAuthCredentials("fresh-refresh-token", "fresh-access-token", 1780831538243));

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            Assert.False(document.RootElement.TryGetProperty("openai-codex", out _));
            Assert.True(document.RootElement.TryGetProperty("providers", out var providers));
            Assert.True(providers.TryGetProperty("openai-codex", out var nested));
            Assert.Equal("fresh-access-token", nested.GetProperty("access").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
