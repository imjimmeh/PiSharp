namespace PiSharp.Ai.Auth;

public interface IOAuthStorage
{
    Task<string?> GetTokenAsync(string provider, CancellationToken cancellationToken = default);
    Task SetTokenAsync(string provider, string token, CancellationToken cancellationToken = default);
    Task RemoveTokenAsync(string provider, CancellationToken cancellationToken = default);
    Task SetOAuthCredentialsAsync(string provider, OAuthCredentials credentials, CancellationToken cancellationToken = default);
    Task<OAuthCredentials?> GetOAuthCredentialsAsync(string provider, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListStoredProvidersAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryOAuthStorage : IOAuthStorage
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OAuthCredentials> _oauthCredentials = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> GetTokenAsync(string provider, CancellationToken cancellationToken = default)
        => Task.FromResult(_tokens.TryGetValue(provider, out var token) ? token : null);

    public Task SetTokenAsync(string provider, string token, CancellationToken cancellationToken = default)
    {
        _tokens[provider] = token;
        return Task.CompletedTask;
    }

    public Task RemoveTokenAsync(string provider, CancellationToken cancellationToken = default)
    {
        _tokens.Remove(provider);
        _oauthCredentials.Remove(provider);
        return Task.CompletedTask;
    }

    public Task SetOAuthCredentialsAsync(string provider, OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        _oauthCredentials[provider] = credentials;
        return Task.CompletedTask;
    }

    public Task<OAuthCredentials?> GetOAuthCredentialsAsync(string provider, CancellationToken cancellationToken = default)
        => Task.FromResult(_oauthCredentials.TryGetValue(provider, out var creds) ? creds : null);

    public Task<IReadOnlyList<string>> ListStoredProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _tokens.Keys) providers.Add(key);
        foreach (var key in _oauthCredentials.Keys) providers.Add(key);
        return Task.FromResult<IReadOnlyList<string>>(providers.ToList());
    }
}
