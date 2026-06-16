using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiSharp.Ai.Auth;

public sealed class OpenAICodexOAuthProvider : IOAuthProvider
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const int CallbackPort = 1455;
    private const string CallbackHost = "localhost";
    private const string CallbackPath = "/auth/callback";
    private const string Scope = "openid profile email offline_access";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string JwtClaimPath = "https://api.openai.com/auth";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public string Id => "openai-codex";

    public string Name => "ChatGPT Plus/Pro (Codex Subscription)";

    public bool UsesCallbackServer => true;

    public string GetApiKey(OAuthCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return credentials.Access;
    }

    public async Task<OAuthCredentials> LoginAsync(OAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);

        var (verifier, challenge) = PkceHelper.Generate();
        var state = CreateState();

        var authUrl = BuildAuthorizeUrl(challenge, state);

        using var server = new OAuthHttpServer(CallbackPort, CallbackHost, CallbackPath);
        try
        {
            await server.StartAsync(cancellationToken);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            throw new InvalidOperationException(
                $"Cannot start OAuth callback server on port {CallbackPort}. The port may be in use.", ex);
        }

        try
        {
            await callbacks.OnAuth(new OAuthAuthInfo(authUrl,
                "A browser window should open. Complete login to finish."));

            var (code, returnedState) = await WaitForAuthorizationInputAsync(
                server.WaitForCodeAsync,
                callbacks,
                state,
                cancellationToken);

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(returnedState))
                throw new InvalidOperationException("Missing authorization code.");

            if (returnedState != state)
                throw new InvalidOperationException("State mismatch.");

            if (callbacks.OnProgress is not null)
                await callbacks.OnProgress("Exchanging authorization code for tokens...");

            return await ExchangeCodeAsync(code, verifier, cancellationToken);
        }
        finally
        {
            server.Dispose();
        }
    }

    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.Refresh,
            ["client_id"] = ClientId
        });

        var response = await Client.PostAsync(TokenUrl, body, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token refresh failed ({(int)response.StatusCode}): {responseText}");

        var data = JsonDocument.Parse(responseText).RootElement;
        var access = data.GetProperty("access_token").GetString()!;
        var refresh = data.GetProperty("refresh_token").GetString()!;
        var expiresIn = data.GetProperty("expires_in").GetInt32();

        var accountId = GetAccountId(access);
        var extra = accountId is not null
            ? new Dictionary<string, object?> { ["accountId"] = accountId }
            : null;

        return new OAuthCredentials(refresh, access, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000, extra);
    }

    private static string BuildAuthorizeUrl(string challenge, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "pi"
        };

        var query = string.Join("&",
            parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return $"{AuthorizeUrl}?{query}";
    }

    private async Task<OAuthCredentials> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri
        });

        var response = await Client.PostAsync(TokenUrl, body, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {responseText}");

        var data = JsonDocument.Parse(responseText).RootElement;
        var access = data.GetProperty("access_token").GetString()!;
        var refresh = data.GetProperty("refresh_token").GetString()!;
        var expiresIn = data.GetProperty("expires_in").GetInt32();

        var accountId = GetAccountId(access);
        var extra = accountId is not null
            ? new Dictionary<string, object?> { ["accountId"] = accountId }
            : null;

        return new OAuthCredentials(refresh, access, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000, extra);
    }

    private static string? GetAccountId(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1];
            var padded = payload + new string('=', (4 - payload.Length % 4) % 4);
            var decoded = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            var json = System.Text.Encoding.UTF8.GetString(decoded);
            var obj = JsonDocument.Parse(json).RootElement;

            if (obj.TryGetProperty(JwtClaimPath, out var authClaim) &&
                authClaim.TryGetProperty("chatgpt_account_id", out var accountId) &&
                accountId.GetString() is { Length: > 0 } id)
                return id;
        }
        catch
        {
            // JWT decode failure is non-fatal
        }

        return null;
    }

    public static string CreateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexStringLower(bytes);
    }

    public static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (string.IsNullOrEmpty(value)) return (null, null);

        if (Uri.TryCreate(value, UriKind.Absolute, out var url))
        {
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            return (query["code"], query["state"]);
        }

        if (value.Contains('#', StringComparison.Ordinal))
        {
            var parts = value.Split('#', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : null);
        }

        if (value.Contains("code=", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = System.Web.HttpUtility.ParseQueryString(value);
            return (parsed["code"], parsed["state"]);
        }

        return (value, null);
    }

    public static async Task<(string? Code, string? State)> WaitForAuthorizationInputAsync(
        Func<TimeSpan, Task<(string Code, string State)?>> waitForCodeAsync,
        OAuthLoginCallbacks callbacks,
        string expectedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waitForCodeAsync);
        ArgumentNullException.ThrowIfNull(callbacks);

        var serverResult = await waitForCodeAsync(TimeSpan.FromMinutes(5));
        if (serverResult is not null)
            return (serverResult.Value.Code, serverResult.Value.State);

        var input = callbacks.OnManualCodeInput is not null
            ? await callbacks.OnManualCodeInput(cancellationToken)
            : await callbacks.OnPrompt(new OAuthPrompt("Paste the authorization code (or full redirect URL):"), cancellationToken);
        var parsed = ParseAuthorizationInput(input);
        if (parsed.State is not null && parsed.State != expectedState)
            throw new InvalidOperationException("State mismatch.");

        return (parsed.Code, parsed.State ?? expectedState);
    }
}
