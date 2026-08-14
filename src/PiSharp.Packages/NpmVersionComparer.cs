namespace PiSharp.Packages;

internal static class NpmVersionComparer
{
    /// <summary>Returns true if <paramref name="installed"/> is strictly older than <paramref name="latest"/>.</summary>
    public static bool IsOlderThan(string? installed, string? latest)
    {
        var a = ParseVersion(installed);
        var b = ParseVersion(latest);
        if (a is null || b is null) return false;

        // Compare major, minor, patch first
        if (a.Value.Major != b.Value.Major) return a.Value.Major < b.Value.Major;
        if (a.Value.Minor != b.Value.Minor) return a.Value.Minor < b.Value.Minor;
        if (a.Value.Patch != b.Value.Patch) return a.Value.Patch < b.Value.Patch;

        // If core versions are equal, pre-releases are older than releases
        return a.Value.IsPreRelease && !b.Value.IsPreRelease;
    }

    private static (int Major, int Minor, int Patch, bool IsPreRelease)? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var dashIndex = version.IndexOf('-');
        var isPreRelease = dashIndex >= 0;
        var core = dashIndex >= 0 ? version[..dashIndex] : version;

        var parts = core.Split('.');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        if (!int.TryParse(parts[2], out var patch)) return null;

        return (major, minor, patch, isPreRelease);
    }
}
