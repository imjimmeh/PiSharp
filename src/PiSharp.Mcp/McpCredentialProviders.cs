using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using PiSharp.Ai.Auth;

namespace PiSharp.Mcp;

/// <summary>
/// Resolves a bearer token from the process environment (<c>config.Auth.EnvVar</c>). Mirrors the
/// core <c>EnvApiKeyDetector</c> convention. Returns null (anonymous) when the variable is unset.
/// </summary>
public sealed class EnvCredentialProvider : IMcpCredentialProvider
{
    public McpAuthKind Kind => McpAuthKind.Env;

    public ValueTask<McpCredential?> ResolveAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
    {
        var envVar = config.Auth?.EnvVar;
        if (string.IsNullOrWhiteSpace(envVar)) return ValueTask.FromResult<McpCredential?>(null);
        var token = Environment.GetEnvironmentVariable(envVar);
        return ValueTask.FromResult<McpCredential?>(string.IsNullOrWhiteSpace(token)
            ? null
            : new McpCredential(token));
    }
}

/// <summary>Resolves a static bearer token from <c>config.Auth.LiteralToken</c>.</summary>
public sealed class LiteralCredentialProvider : IMcpCredentialProvider
{
    public McpAuthKind Kind => McpAuthKind.Literal;

    public ValueTask<McpCredential?> ResolveAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
    {
        var token = config.Auth?.LiteralToken;
        return ValueTask.FromResult<McpCredential?>(string.IsNullOrWhiteSpace(token)
            ? null
            : new McpCredential(token));
    }
}

/// <summary>
/// Resolves OAuth credentials from the shared auth store under <c>mcp:&lt;serverName&gt;</c>
/// (prime's <c>mcp:&lt;name&gt;</c> convention). When no credentials are stored, the HTTP transport
/// runs the SDK's MCP OAuth flow (discovery, dynamic registration, PKCE, browser consent) and
/// persists the result through <see cref="OAuthStorageTokenCache"/>.
/// </summary>
public sealed class OAuthCredentialProvider : IMcpCredentialProvider
{
    public const string ProviderKeyPrefix = "mcp:";

    public McpAuthKind Kind => McpAuthKind.OAuth;

    public static string ProviderKey(string serverName) => ProviderKeyPrefix + serverName;

    public async ValueTask<McpCredential?> ResolveAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
    {
        var storage = context.AuthStorage;
        if (storage is null) return null;
        var credentials = await storage.GetOAuthCredentialsAsync(ProviderKey(config.Name), cancellationToken);
        if (credentials is null) return null;
        return new McpCredential(credentials.Access, credentials.Refresh, credentials.Expires);
    }

    /// <summary>
    /// Configures the SDK OAuth flow on HTTP transport options. Only invoked when no usable stored
    /// credentials exist. The callback handler shell-opens the authorization URL (failing
    /// gracefully on headless hosts) and captures the loopback callback; the SDK then validates
    /// the returned state and RFC 9207 issuer.
    /// </summary>
    public void ConfigureOAuth(HttpClientTransportOptions options, McpServerConfig config, McpTransportContext context)
    {
        var storage = context.AuthStorage;
        if (storage is null) return; // anonymous; transport will surface "auth required" on 401
        var redirectUri = new Uri($"http://127.0.0.1:{McpOAuthRedirect.PickFreePort()}/oauth/callback");
        options.OAuth = new ClientOAuthOptions
        {
            ClientId = string.IsNullOrWhiteSpace(config.Auth?.ClientId) ? null : config.Auth!.ClientId,
            RedirectUri = redirectUri,
            TokenCache = new OAuthStorageTokenCache(storage, ProviderKey(config.Name)),
            AuthorizationCallbackHandler = async (callbackContext, cancellationToken) =>
            {
                await context.OpenUrlAsync(callbackContext.AuthorizationUri.ToString(), cancellationToken);
                return await McpOAuthRedirect.CaptureAsync(callbackContext.RedirectUri, cancellationToken);
            }
        };
    }
}

/// <summary>
/// Bridges the SDK's <see cref="ITokenCache"/> to the shared <see cref="IOAuthStorage"/> so OAuth
/// tokens live in <c>~/.pi/agent/auth.json</c> under <c>mcp:&lt;name&gt;</c>.
public sealed class OAuthStorageTokenCache(IOAuthStorage storage, string providerKey) : ITokenCache
{
    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var credentials = await storage.GetOAuthCredentialsAsync(providerKey, cancellationToken);
        if (credentials is null) return null;
        var expiresIn = (int)Math.Max(0, credentials.Expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = credentials.Access,
            RefreshToken = credentials.Refresh,
            ExpiresIn = expiresIn,
            ObtainedAt = DateTimeOffset.UtcNow.AddSeconds(-expiresIn)
        };
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var expires = tokens.ObtainedAt.ToUnixTimeSeconds() + Math.Max(0, tokens.ExpiresIn ?? 0);
        await storage.SetOAuthCredentialsAsync(providerKey,
            new OAuthCredentials(tokens.RefreshToken ?? string.Empty, tokens.AccessToken ?? string.Empty, expires),
            cancellationToken);
    }
}
