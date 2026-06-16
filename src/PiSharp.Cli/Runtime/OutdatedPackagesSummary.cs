using PiSharp.Cli.Packages;

namespace PiSharp.Cli.Runtime;

public static class OutdatedPackagesSummary
{
    public static string? Format(IReadOnlyList<OutdatedPackageInfo> outdated)
    {
        if (outdated.Count == 0) return null;
        var packages = string.Join(", ", outdated.Select(p => $"{p.Name} ({p.InstalledVersion} \u2192 {p.LatestVersion})"));
        return $"Outdated extensions: {packages}. Run `pi update` to upgrade.";
    }
}
