using System.Text.RegularExpressions;

namespace PiSharp.Compatibility.Settings;

/// <summary>A resolved PiSharp profile: a name and the directory every user-level root relocates under.</summary>
public sealed record PiProfile(string Name, string RootDirectory)
{
    /// <summary>Profile root: ~/.pi/PiSharp/profiles/&lt;name&gt; (full path).</summary>
    public static string ResolveRootDirectory(string profileName, string homeDirectory)
        => Path.Combine(homeDirectory, ".pi", "PiSharp", "profiles", profileName);
}

public static partial class PiProfiles
{
    public const string EnvironmentVariable = "PISHARP_PROFILE";
    public const string LegacyEnvironmentVariable = "PI_PROFILE";   // pi-compat fallback
    public const string DefaultProfileName = "default";             // reserved; == no profile

    private static readonly Regex NameRegex = CreateNameRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex CreateNameRegex();

    /// <summary>CLI flag first, then PISHARP_PROFILE, then PI_PROFILE, then null. Validates + computes root.</summary>
    public static PiProfile? Resolve(string? cliProfileName, string? homeDirectory = null)
    {
        var name = cliProfileName;
        if (string.IsNullOrWhiteSpace(name)) name = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(name)) name = Environment.GetEnvironmentVariable(LegacyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, DefaultProfileName, StringComparison.Ordinal)) return null;

        var home = string.IsNullOrWhiteSpace(homeDirectory) ? PiAgentPaths.DefaultHomeDirectory() : homeDirectory;
        if (!IsValidName(name, out _)) return null;
        return new PiProfile(name, PiProfile.ResolveRootDirectory(name, home));
    }

    /// <summary>^[a-z0-9][a-z0-9-]{0,63}$ ; rejects reserved "default". Error text or null.</summary>
    public static bool IsValidName(string? name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Profile name must be provided.";
            return false;
        }

        if (string.Equals(name, DefaultProfileName, StringComparison.Ordinal))
        {
            error = $"Profile name '{name}' is reserved.";
            return false;
        }

        if (!NameRegex.IsMatch(name))
        {
            error = $"Profile name '{name}' is invalid. Use lowercase letters, digits, and hyphens (max 64 chars, must start with a letter or digit).";
            return false;
        }

        error = null;
        return true;
    }
}
