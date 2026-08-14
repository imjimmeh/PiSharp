namespace PiSharp.Extensions;

/// <summary>Package source reference classification for the runtime package API.</summary>
public sealed record ExtensionPackageSource(string Reference, bool Local = false);

/// <summary>Update request mapped 1:1 to <c>PackageUpdateRequest</c> (GAP-55).</summary>
public sealed record ExtensionPackageUpdateRequest(
    string? Source = null,
    bool Extensions = false,
    string? ExtensionSource = null,
    bool Force = false,
    bool Offline = false);

/// <summary>An installed package as reported by the runtime package API.</summary>
public sealed record ExtensionInstalledPackage(string Source, string Layer);

/// <summary>Outcome of an install/update operation.</summary>
public sealed record ExtensionPackageResult(bool Success, string? Error = null, string? Path = null);

/// <summary>
/// Runtime package management surface (GAP-55). Backed by the shared package
/// engine (<c>IPackageCommandRunner</c>) adapted in <c>PiSharp.Runtime</c>;
/// successful install/update/remove trigger a hot reload and emit
/// <c>extensions_changed</c>.
/// </summary>
public interface IExtensionPackageApi
{
    Task<ExtensionPackageResult> InstallAsync(string reference, bool local = false, bool force = false, bool offline = false, CancellationToken ct = default);
    Task<ExtensionPackageResult> UpdateAsync(ExtensionPackageUpdateRequest request, CancellationToken ct = default);
    Task<bool> RemoveAsync(string reference, bool local = false, CancellationToken ct = default);
    Task<IReadOnlyList<ExtensionInstalledPackage>> ListAsync(CancellationToken ct = default);
}
