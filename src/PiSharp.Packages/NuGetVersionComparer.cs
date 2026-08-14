namespace PiSharp.Packages;

internal enum VersionStability { Stable, PreRelease, Unparseable }

/// <summary>NuGet/SemVer-style comparison. Mirrors NpmVersionComparer semantics.</summary>
internal static class NuGetVersionComparer
{
    /// <summary>True when installed is strictly older than latest.
    /// Unparseable or prerelease installed ⇒ false (skip the check).</summary>
    public static bool IsOlderThan(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest)) return false;
        if (ParseStability(installed) != VersionStability.Stable) return false;
        if (ParseStability(latest) == VersionStability.Unparseable) return false;

        var a = ParseVersion(installed);
        var b = ParseVersion(latest);
        if (a is null || b is null) return false;

        if (a.Value.Major != b.Value.Major) return a.Value.Major < b.Value.Major;
        if (a.Value.Minor != b.Value.Minor) return a.Value.Minor < b.Value.Minor;
        if (a.Value.Patch != b.Value.Patch) return a.Value.Patch < b.Value.Patch;
        // Equal core version: installed (known stable) is never strictly older than latest.
        return false;
    }

    /// <summary>Parses the stability class of a version string.</summary>
    internal static VersionStability ParseStability(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return VersionStability.Unparseable;
        var firstDash = version.IndexOf('-');
        var firstPlus = version.IndexOf('+');
        var first = firstDash >= 0 ? firstDash : firstPlus;
        var core = first >= 0 ? version[..first] : version;
        var parts = core.Split('.');
        if (parts.Length < 3) return VersionStability.Unparseable;
        if (!int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _) || !int.TryParse(parts[2], out _))
            return VersionStability.Unparseable;
        return version.Contains('-') ? VersionStability.PreRelease : VersionStability.Stable;
    }

    private static (int Major, int Minor, int Patch)? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var firstDash = version.IndexOf('-');
        var firstPlus = version.IndexOf('+');
        var first = firstDash >= 0 ? firstDash : firstPlus;
        var core = first >= 0 ? version[..first] : version;
        var parts = core.Split('.');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        if (!int.TryParse(parts[2], out var patch)) return null;
        return (major, minor, patch);
    }

    internal static string? Normalize(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
