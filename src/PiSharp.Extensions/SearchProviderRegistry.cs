namespace PiSharp.Extensions;

/// <summary>
/// Runtime-wide registry of <see cref="ISearchProvider"/>s. The runtime owns a
/// single instance and exposes it to extensions through
/// <see cref="IExtensionApi.Search"/>.
/// </summary>
public sealed class SearchProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ISearchProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a provider under its <see cref="ISearchProvider.Id"/>.
    /// Duplicate id throws <see cref="InvalidOperationException"/> unless
    /// <paramref name="overrideExisting"/> is true.
    /// </summary>
    public void Register(ISearchProvider provider, bool overrideExisting = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id))
            throw new ArgumentException("Search provider id must not be empty.", nameof(provider));
        lock (_gate)
        {
            if (!overrideExisting && _providers.ContainsKey(provider.Id))
                throw new InvalidOperationException($"Search provider '{provider.Id}' is already registered.");
            _providers[provider.Id] = provider;
        }
    }

    /// <summary>Removes the provider with <paramref name="providerId"/>; returns false when none was registered.</summary>
    public bool Unregister(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        lock (_gate) return _providers.Remove(providerId);
    }

    /// <summary>Returns the provider with <paramref name="providerId"/>, or null when not registered.</summary>
    public ISearchProvider? TryGet(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        lock (_gate) return _providers.TryGetValue(providerId, out var provider) ? provider : null;
    }

    /// <summary>All registered providers (unordered).</summary>
    public IReadOnlyList<ISearchProvider> Providers
    {
        get { lock (_gate) return _providers.Values.ToArray(); }
    }
}
