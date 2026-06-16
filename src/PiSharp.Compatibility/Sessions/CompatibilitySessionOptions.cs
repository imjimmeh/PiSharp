using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Sessions;

public sealed record CompatibilitySessionOptions(
    string SessionsRoot,
    bool WriteLeafEntries = false,
    bool TolerateUnknownEntries = true,
    bool MigrateLegacyHeaders = true)
{
    public static CompatibilitySessionOptions FromPaths(PiAgentPaths paths, string? sessionDirOverride = null)
        => new(string.IsNullOrWhiteSpace(sessionDirOverride) ? paths.SessionsRoot : sessionDirOverride);
}
