using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Models;

namespace PiSharp.Ai.Auth;

public sealed record ProviderCredentialResult(
    string? ApiKey = null,
    string? BearerToken = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool IsAuthenticated = false);

public interface IProviderCredentialResolver
{
    Task<ProviderCredentialResult> ResolveAsync(
        ModelDescriptor model,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class ProviderCredentialResolver : IProviderCredentialResolver
{
    private static readonly IReadOnlyDictionary<string, string[]> DefaultProviderAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["openai-codex"] = ["openai"],
        ["openai-codex-responses"] = ["openai-codex", "openai"]
    };

    private readonly IOAuthStorage? _oauthStorage;
    private readonly IReadOnlyDictionary<string, string> _ambientHeaders;
    private readonly IReadOnlyDictionary<string, string[]> _providerAliases;

    public ProviderCredentialResolver(
        IOAuthStorage? oauthStorage = null,
        IReadOnlyDictionary<string, string>? ambientHeaders = null,
        IReadOnlyDictionary<string, string[]>? providerAliases = null)
    {
        _oauthStorage = oauthStorage;
        _ambientHeaders = ambientHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _providerAliases = providerAliases ?? DefaultProviderAliases;
    }

    public async Task<ProviderCredentialResult> ResolveAsync(
        ModelDescriptor model,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var providerConfig = ModelRegistry.GetProviderConfig(model.Provider);

        Merge(headers, _ambientHeaders);
        Merge(headers, EnvApiKeyDetector.GetProviderHeaders(model.Provider));
        if (providerConfig?.Headers is not null) Merge(headers, providerConfig.Headers);
        if (model.Headers is not null) Merge(headers, model.Headers);
        if (options.Headers is not null) Merge(headers, options.Headers);

        var apiKey = options.ApiKey;
        var bearerToken = string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (_oauthStorage is not null)
            {
                bearerToken = await ResolveOAuthTokenAsync(model.Provider, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                apiKey = ResolveConfiguredApiKey(model.ApiKey ?? providerConfig?.ApiKey) ?? EnvApiKeyDetector.GetEnvApiKey(model.Provider);
            }
        }

        var isAmbientAuthenticated = string.Equals(apiKey, EnvApiKeyDetector.AuthenticatedMarker, StringComparison.Ordinal);
        if (isAmbientAuthenticated)
        {
            apiKey = null;
        }

        var useAuthHeader = model.AuthHeader ?? providerConfig?.AuthHeader ?? true;
        if (useAuthHeader && !string.IsNullOrWhiteSpace(apiKey) && !headers.ContainsKey("Authorization"))
        {
            headers["Authorization"] = $"Bearer {apiKey}";
        }
        else if (useAuthHeader && !string.IsNullOrWhiteSpace(bearerToken))
        {
            headers["Authorization"] = $"Bearer {bearerToken}";
            apiKey = null;
        }

        return new ProviderCredentialResult(
            ApiKey: apiKey,
            BearerToken: string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken,
            Headers: headers,
            IsAuthenticated: isAmbientAuthenticated || !string.IsNullOrWhiteSpace(apiKey) || !string.IsNullOrWhiteSpace(bearerToken));
    }

    private async Task<string?> ResolveOAuthTokenAsync(string provider, CancellationToken cancellationToken)
    {
        foreach (var candidate in ProviderCandidates(provider))
        {
            var token = await _oauthStorage!.GetTokenAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token)) return token;
        }
        return null;
    }

    private IEnumerable<string> ProviderCandidates(string provider)
    {
        yield return provider;
        if (!_providerAliases.TryGetValue(provider, out var aliases)) yield break;
        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.OrdinalIgnoreCase)) yield return alias;
    }

    private static string? ResolveConfiguredApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var envValue = Environment.GetEnvironmentVariable(apiKey);
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue;
        return LooksLikeEnvironmentVariable(apiKey) ? null : apiKey;
    }

    private static bool LooksLikeEnvironmentVariable(string value)
        => value.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_') && value.Contains('_', StringComparison.Ordinal);

    private static void Merge(IDictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var header in source)
        {
            target[header.Key] = header.Value;
        }
    }
}
