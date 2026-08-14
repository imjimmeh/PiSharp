using PiSharp.Cli.Packages;

namespace PiSharp.Cli.Runtime;

public static class SelfUpdateSummary
{
    /// <summary>→ "PiSharp 1.2.3 is available (installed 1.1.0). Run `pisharp update self` to upgrade."
    /// null when there is nothing to report.</summary>
    public static string? Format(OutdatedSelfInfo? info)
    {
        if (info is null) return null;
        return $"PiSharp {info.LatestVersion} is available (installed {info.InstalledVersion}). Run `pisharp update self` to upgrade.";
    }
}
