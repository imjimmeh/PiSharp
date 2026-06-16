using System.Collections.Concurrent;

namespace PiSharp.Ai.Auth;

public static class OAuthProviderRegistry
{
    private static readonly ConcurrentDictionary<string, IOAuthProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IOAuthProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Id] = provider;
    }

    public static IOAuthProvider? Get(string id)
    {
        _providers.TryGetValue(id, out var provider);
        return provider;
    }

    public static void Unregister(string id)
    {
        _providers.TryRemove(id, out _);
    }

    public static IReadOnlyList<IOAuthProvider> GetAll()
        => _providers.Values.ToArray();

    public static bool IsOAuthProvider(string providerId)
        => _providers.ContainsKey(providerId);
}
