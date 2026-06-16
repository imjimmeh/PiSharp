using System.Net;
using System.Text.Json;

namespace PiSharp.Ai.Auth;

public sealed class AnthropicOAuthProvider : IOAuthProvider
{
    private const string ClientId = "OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const int CallbackPort = 53692;
    private const string CallbackHost = "127.0.0.1";
    private const string Scopes = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public string Id => "anthropic";

    public string Name => "Anthropic (Claude Pro/Max)";

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
        var redirectUri = $"http://{CallbackHost}:{CallbackPort}/callback";

        using var server = new OAuthHttpServer(CallbackPort, CallbackHost);
        try
        {
            await server.StartAsync(cancellationToken);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            throw new InvalidOperationException(
                $"Cannot start OAuth callback server on port {CallbackPort}. The port may be in use or requires elevated permissions. Try again, or use a different provider.",
                ex);
        }

        try
        {
            var authUrl = BuildAuthorizeUrl(challenge, verifier, redirectUri);

            await callbacks.OnAuth(new OAuthAuthInfo(authUrl,
                "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here."));

            string? code;
            string? state;

            if (callbacks.OnManualCodeInput is not null)
            {
                var manualTask = callbacks.OnManualCodeInput(cancellationToken);
                var serverResult = await server.WaitForCodeAsync(TimeSpan.FromMinutes(5));

                if (serverResult is not null)
                {
                    code = serverResult.Value.Code;
                    state = serverResult.Value.State;
                }
                else
                {
                    server.CancelWait();
                    var manualInput = await manualTask;
                    var parsed = ParseAuthorizationInput(manualInput);
                    code = parsed.Code;
                    state = parsed.State ?? verifier;
                }
            }
            else
            {
                var serverResult = await server.WaitForCodeAsync(TimeSpan.FromMinutes(5));
                if (serverResult is not null)
                {
                    code = serverResult.Value.Code;
                    state = serverResult.Value.State;
                }
                else
                {
                    var input = await callbacks.OnPrompt(
                        new OAuthPrompt("Paste the authorization code or full redirect URL:", redirectUri),
                        cancellationToken);
                    var parsed = ParseAuthorizationInput(input);
                    if (parsed.State is not null && parsed.State != verifier)
                        throw new InvalidOperationException("OAuth state mismatch.");
                    code = parsed.Code;
                    state = parsed.State ?? verifier;
                }
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                throw new InvalidOperationException("Missing authorization code.");

            if (callbacks.OnProgress is not null)
                await callbacks.OnProgress("Exchanging authorization code for tokens...");

            return await ExchangeCodeAsync(code, state, verifier, redirectUri, cancellationToken);
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
            ["client_id"] = ClientId,
            ["refresh_token"] = credentials.Refresh
        });

        var response = await Client.PostAsync(TokenUrl, body, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token refresh failed ({(int)response.StatusCode}): {responseText}");

        var data = JsonDocument.Parse(responseText).RootElement;
        var access = data.GetProperty("access_token").GetString()!;
        var refresh = data.GetProperty("refresh_token").GetString()!;
        var expiresIn = data.GetProperty("expires_in").GetInt32();

        return new OAuthCredentials(
            refresh,
            access,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000 - 5 * 60 * 1000);
    }

    private static string BuildAuthorizeUrl(string challenge, string state, string redirectUri)
    {
        var parameters = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        };

        var query = string.Join("&",
            parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return $"{AuthorizeUrl}?{query}";
    }

    private async Task<OAuthCredentials> ExchangeCodeAsync(string code, string state, string verifier, string redirectUri, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["state"] = state,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier
        });

        var response = await Client.PostAsync(TokenUrl, body, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {responseText}");

        var data = JsonDocument.Parse(responseText).RootElement;
        var access = data.GetProperty("access_token").GetString()!;
        var refresh = data.GetProperty("refresh_token").GetString()!;
        var expiresIn = data.GetProperty("expires_in").GetInt32();

        return new OAuthCredentials(
            refresh,
            access,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000 - 5 * 60 * 1000);
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
}
