namespace PiSharp.Extensions;

/// <summary>
/// Registration surface for search providers exposed to extensions via
/// <see cref="IExtensionApi.Search"/>. Wraps the runtime-wide
/// <see cref="SearchProviderRegistry"/>.
/// </summary>
public interface IExtensionSearchApi
{
    /// <summary>
    /// Registers a search provider in the runtime-wide registry. Duplicate id
    /// throws unless <paramref name="overrideExisting"/>. The returned
    /// <see cref="IDisposable"/> unregisters the provider when disposed.
    /// </summary>
    IDisposable RegisterProvider(ISearchProvider provider, bool overrideExisting = false);

    /// <summary>All registered providers.</summary>
    IReadOnlyList<ISearchProvider> Providers { get; }

    /// <summary>The provider with <paramref name="providerId"/>, or null when not registered.</summary>
    ISearchProvider? GetProvider(string providerId);
}
