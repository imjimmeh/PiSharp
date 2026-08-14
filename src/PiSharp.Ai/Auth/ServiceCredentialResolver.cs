namespace PiSharp.Ai.Auth;

/// <summary>
/// Options controlling service (non-model) credential resolution, e.g. search
/// provider API keys. <see cref="ConfiguredKey"/> follows the same convention
/// as model configured keys: an env-var NAME is read from the environment
/// (default form), a literal value is used directly, and the
/// <see cref="EnvApiKeyDetector.AuthenticatedMarker"/> ("&lt;authenticated&gt;")
/// means "use ambient only".
/// </summary>
public sealed record ServiceCredentialOptions(
    string? ConfiguredKey = null,
    bool UseAuthHeader = true);

/// <summary>
/// Resolves API keys for non-model services (search providers and the like)
/// through the shared credential conventions: a configured key (env-var-name /
/// literal / authenticated marker) first, then the ambient environment map via
/// <see cref="EnvApiKeyDetector"/>.
/// </summary>
public interface IServiceCredentialResolver
{
    Task<ProviderCredentialResult> ResolveAsync(
        string serviceId,
        ServiceCredentialOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class ServiceCredentialResolver : IServiceCredentialResolver
{
    public Task<ProviderCredentialResult> ResolveAsync(
        string serviceId,
        ServiceCredentialOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var configuredKey = options?.ConfiguredKey;
        string? apiKey = null;
        var isAuthenticated = false;

        if (!string.IsNullOrWhiteSpace(configuredKey)
            && !string.Equals(configuredKey, EnvApiKeyDetector.AuthenticatedMarker, StringComparison.Ordinal))
        {
            apiKey = ResolveConfiguredApiKey(configuredKey);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var ambient = EnvApiKeyDetector.GetEnvApiKey(serviceId);
            if (!string.IsNullOrWhiteSpace(ambient)) apiKey = ambient;
        }

        var isAmbientAuthenticated = string.Equals(apiKey, EnvApiKeyDetector.AuthenticatedMarker, StringComparison.Ordinal);
        if (isAmbientAuthenticated)
        {
            apiKey = null;
            isAuthenticated = true;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Merge(headers, EnvApiKeyDetector.GetProviderHeaders(serviceId));

        var useAuthHeader = options?.UseAuthHeader ?? true;
        if (useAuthHeader && !string.IsNullOrWhiteSpace(apiKey) && !headers.ContainsKey("Authorization"))
        {
            headers["Authorization"] = $"Bearer {apiKey}";
        }

        return Task.FromResult(new ProviderCredentialResult(
            ApiKey: apiKey,
            Headers: headers,
            IsAuthenticated: isAuthenticated || !string.IsNullOrWhiteSpace(apiKey)));
    }

    /// <summary>
    /// A configured key in env-var-name form is read from the environment; a
    /// value that does not look like an env-var name is treated as a literal key.
    /// </summary>
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
        foreach (var header in source) target[header.Key] = header.Value;
    }
}
