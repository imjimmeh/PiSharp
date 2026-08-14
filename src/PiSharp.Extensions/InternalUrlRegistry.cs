namespace PiSharp.Extensions;

/// <summary>
/// Runtime-wide registry of internal URL scheme resolvers. The runtime owns a
/// single instance, injects it into read-tool construction and exposes it to
/// extensions through <see cref="IExtensionUrlApi"/>.
/// </summary>
public sealed class InternalUrlRegistry
{
    private readonly Dictionary<string, IInternalUrlResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a resolver for its <see cref="IInternalUrlResolver.Scheme"/>.
    /// Duplicate registration (same scheme) throws
    /// <see cref="InvalidOperationException"/> unless <paramref name="overrideExisting"/> is true.
    /// </summary>
    public void Register(IInternalUrlResolver resolver, bool overrideExisting = false)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var scheme = resolver.Scheme;
        if (string.IsNullOrWhiteSpace(scheme)) throw new ArgumentException("Resolver scheme must not be empty.", nameof(resolver));
        scheme = scheme.ToLowerInvariant();
        lock (_resolvers)
        {
            if (!overrideExisting && _resolvers.ContainsKey(scheme))
                throw new InvalidOperationException($"Internal URL scheme '{scheme}' is already registered.");
            _resolvers[scheme] = resolver;
        }
    }

    /// <summary>Removes the resolver for <paramref name="scheme"/>; returns false when none was registered.</summary>
    public bool Unregister(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme)) return false;
        lock (_resolvers) return _resolvers.Remove(scheme.ToLowerInvariant());
    }

    public bool TryGet(string scheme, out IInternalUrlResolver resolver)
    {
        lock (_resolvers) return _resolvers.TryGetValue(scheme, out resolver!);
    }

    public IReadOnlyList<string> Schemes
    {
        get { lock (_resolvers) return _resolvers.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(); }
    }
}
