using System.Text.Json;

namespace PiSharp.Ai.Auth;

public sealed class GitHubCopilotOAuthProvider : IOAuthProvider
{
    private const string ClientId = "SXYxLmI1MDdhMDhjODdlY2ZlOTg=";
    private const string DefaultDomain = "github.com";

    private static readonly Dictionary<string, string> CopilotHeaders = new()
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat"
    };

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public string Id => "github-copilot";

    public string Name => "GitHub Copilot";

    public bool UsesCallbackServer => false;

    public string GetApiKey(OAuthCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return credentials.Access;
    }

    public async Task<OAuthCredentials> LoginAsync(OAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);

        var input = await callbacks.OnPrompt(
            new OAuthPrompt("GitHub Enterprise URL/domain (blank for github.com)", "company.ghe.com", AllowEmpty: true),
            cancellationToken);

        var trimmed = input.Trim();
        var enterpriseDomain = NormalizeDomain(input);
        if (trimmed.Length > 0 && enterpriseDomain is null)
            throw new InvalidOperationException("Invalid GitHub Enterprise URL/domain.");

        var domain = enterpriseDomain ?? DefaultDomain;

        var device = await StartDeviceFlowAsync(domain, cancellationToken);
        await callbacks.OnAuth(new OAuthAuthInfo(device.VerificationUri, $"Enter code: {device.UserCode}"));

        if (callbacks.OnProgress is not null) await callbacks.OnProgress("Waiting for authorization...");
        var githubAccessToken = await PollForAccessTokenAsync(domain, device, cancellationToken);

        if (callbacks.OnProgress is not null) await callbacks.OnProgress("Exchanging GitHub token for Copilot token...");
        var credentials = await ExchangeForCopilotTokenAsync(githubAccessToken, enterpriseDomain, cancellationToken);

        if (callbacks.OnProgress is not null) await callbacks.OnProgress("Enabling models...");
        await EnableAllModelsAsync(credentials.Access, enterpriseDomain, modelId => { if (callbacks.OnProgress is not null) _ = callbacks.OnProgress($"Enabled model: {modelId}"); });

        return credentials;
    }

    public async Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var enterpriseDomain = credentials.Extra?.GetValueOrDefault("enterpriseDomain") as string;
        return await ExchangeForCopilotTokenAsync(credentials.Refresh, enterpriseDomain, cancellationToken);
    }

    public static string? NormalizeDomain(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;

        try
        {
            var uri = trimmed.Contains("://", StringComparison.Ordinal)
                ? new Uri(trimmed)
                : new Uri($"https://{trimmed}");
            return uri.Host;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetBaseUrlFromToken(string token)
    {
        var match = System.Text.RegularExpressions.Regex.Match(token, @"proxy-ep=([^;]+)");
        if (!match.Success) return null;

        var proxyHost = match.Groups[1].Value;
        var apiHost = System.Text.RegularExpressions.Regex.Replace(proxyHost, @"^proxy\.", "api.");
        return $"https://{apiHost}";
    }

    public static string GetBaseUrl(string? token = null, string? enterpriseDomain = null)
    {
        if (token is not null)
        {
            var urlFromToken = GetBaseUrlFromToken(token);
            if (urlFromToken is not null) return urlFromToken;
        }

        if (enterpriseDomain is not null)
            return $"https://copilot-api.{enterpriseDomain}";

        return "https://api.individual.githubcopilot.com";
    }

    private static (string DeviceCodeUrl, string AccessTokenUrl, string CopilotTokenUrl) GetUrls(string domain) => (
        $"https://{domain}/login/device/code",
        $"https://{domain}/login/oauth/access_token",
        $"https://api.{domain}/copilot_internal/v2/token"
    );

    private async Task<DeviceCodeResponse> StartDeviceFlowAsync(string domain, CancellationToken ct)
    {
        var urls = GetUrls(domain);
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, urls.DeviceCodeUrl)
        {
            Content = body
        };
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", "GitHubCopilotChat/0.35.0");

        var response = await Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var data = JsonDocument.Parse(json).RootElement;

        return new DeviceCodeResponse(
            data.GetProperty("device_code").GetString()!,
            data.GetProperty("user_code").GetString()!,
            data.GetProperty("verification_uri").GetString()!,
            data.GetProperty("interval").GetInt32(),
            data.GetProperty("expires_in").GetInt32());
    }

    private async Task<string> PollForAccessTokenAsync(string domain, DeviceCodeResponse device, CancellationToken ct)
    {
        var urls = GetUrls(domain);
        var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + device.ExpiresIn * 1000;
        var intervalMs = Math.Max(1000, device.Interval * 1000);

        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(intervalMs, ct);

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            var request = new HttpRequestMessage(HttpMethod.Post, urls.AccessTokenUrl) { Content = body };
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("User-Agent", "GitHubCopilotChat/0.35.0");

            var response = await Client.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonDocument.Parse(json).RootElement;

            if (data.TryGetProperty("access_token", out var accessToken))
                return accessToken.GetString()!;

            if (data.TryGetProperty("error", out var error))
            {
                var errorType = error.GetString();
                if (errorType == "authorization_pending")
                    continue;

                if (errorType == "slow_down")
                {
                    intervalMs = Math.Max(1000, intervalMs + 5000);
                    continue;
                }
            }
        }

        throw new TimeoutException("Device flow timed out");
    }

    private async Task<OAuthCredentials> ExchangeForCopilotTokenAsync(string githubToken, string? enterpriseDomain, CancellationToken ct)
    {
        var domain = enterpriseDomain ?? DefaultDomain;
        var urls = GetUrls(domain);

        var request = new HttpRequestMessage(HttpMethod.Get, urls.CopilotTokenUrl);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Authorization", $"Bearer {githubToken}");
        foreach (var (key, value) in CopilotHeaders)
            request.Headers.Add(key, value);

        var response = await Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var data = JsonDocument.Parse(json).RootElement;

        var token = data.GetProperty("token").GetString()!;
        var expiresAt = data.GetProperty("expires_at").GetInt64();

        var extra = new Dictionary<string, object?>();
        if (enterpriseDomain is not null)
            extra["enterpriseDomain"] = enterpriseDomain;

        return new OAuthCredentials(
            githubToken,
            token,
            expiresAt * 1000 - 5 * 60 * 1000,
            extra.Count > 0 ? extra : null);
    }

    private static async Task EnableAllModelsAsync(string token, string? enterpriseDomain, Action<string>? onModelEnabled)
    {
        var baseUrl = GetBaseUrl(token, enterpriseDomain);

        var knownModels = new[]
        {
            "claude-sonnet-4-20250515", "claude-opus-4-20250514",
            "gpt-4o", "gpt-4o-mini", "o1", "o1-mini", "o3-mini",
            "grok-1", "grok-2"
        };

        foreach (var modelId in knownModels)
        {
            try
            {
                var url = $"{baseUrl}/models/{modelId}/policy";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", $"Bearer {token}");
                foreach (var (key, value) in CopilotHeaders)
                    request.Headers.Add(key, value);
                request.Headers.Add("openai-intent", "chat-policy");
                request.Headers.Add("x-interaction-type", "chat-policy");
                request.Content = new StringContent("{\"state\":\"enabled\"}", System.Text.Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    onModelEnabled?.Invoke(modelId);
            }
            catch
            {
                // Model enablement is best-effort
            }
        }
    }

    private sealed record DeviceCodeResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int Interval,
        int ExpiresIn);
}
