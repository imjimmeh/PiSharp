namespace PiSharp.Cli.Packages;

public sealed record OutdatedSelfInfo(string InstalledVersion, string LatestVersion);

/// <summary>Checks whether a newer PiSharp is available on the default feed.</summary>
public sealed class SelfUpdateChecker(INuGetRegistryClient registryClient)
{
    /// <summary>null when skipped (prerelease/unparseable installed, offline, or network failure).</summary>
    public async Task<OutdatedSelfInfo?> CheckAsync(string? installedVersion, bool offline, CancellationToken cancellationToken)
    {
        if (offline) return null;
        if (string.IsNullOrWhiteSpace(installedVersion)) return null;
        if (NuGetVersionComparer.ParseStability(installedVersion) != VersionStability.Stable) return null;

        var latest = await registryClient.GetLatestStableVersionAsync("pisharp.cli", cancellationToken);
        if (latest is null) return null;
        if (!NuGetVersionComparer.IsOlderThan(installedVersion, latest)) return null;

        return new OutdatedSelfInfo(installedVersion, latest);
    }
}
