namespace PiSharp.Extensions;

/// <summary>
/// Registration surface for internal URL scheme resolvers exposed to
/// extensions via <see cref="IExtensionApi.Urls"/>. Wraps the runtime-wide
/// <see cref="InternalUrlRegistry"/>.
/// </summary>
public interface IExtensionUrlApi
{
    /// <summary>
    /// Registers a resolver for a scheme in the runtime-wide registry.
    /// Duplicate scheme throws unless <paramref name="overrideExisting"/>.
    /// </summary>
    void RegisterResolver(IInternalUrlResolver resolver, bool overrideExisting = false);

    /// <summary>Lists currently registered schemes (for discovery/ADMIN surfaces).</summary>
    IReadOnlyList<string> Schemes { get; }
}
