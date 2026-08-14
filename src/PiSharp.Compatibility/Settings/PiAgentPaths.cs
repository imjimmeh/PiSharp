namespace PiSharp.Compatibility.Settings;

public sealed record PiAgentPaths(
    string HomeDirectory,
    string Cwd,
    string GlobalAgentDirectory,
    string ProjectPiDirectory,
    string GlobalSettingsPath,
    string ProjectSettingsPath,
    string AuthPath,
    string ModelsPath,
    string KeybindingsPath,
    string SessionsRoot,
    string SessionDirectory,
    string GlobalPiSharpDirectory,
    string ProjectPiSharpDirectory,
    string GlobalPiSharpSettingsPath,
    string ProjectPiSharpSettingsPath,
    string GlobalExtensionDirectory,
    string? Profile = null)
{
    public static PiAgentPaths FromCwd(string cwd, string? homeDirectory = null, string? profile = null)
    {
        var fullCwd = Path.GetFullPath(cwd);
        var home = Path.GetFullPath(string.IsNullOrWhiteSpace(homeDirectory) ? DefaultHomeDirectory() : homeDirectory);

        // Profile root: ~/.pi/PiSharp/profiles/<name>. When no profile (null/empty/"default"),
        // layout is byte-identical to the legacy default.
        var profileRoot = ResolveProfileRoot(home, profile);
        var global = profileRoot is null
            ? Path.Combine(home, ".pi", "agent")
            : Path.Combine(profileRoot, "agent");
        var project = Path.Combine(fullCwd, ".pi");
        var globalPiSharp = profileRoot ?? Path.Combine(home, ".pi", "PiSharp");
        var projectPiSharp = Path.Combine(project, "PiSharp");
        var globalExtensions = profileRoot is null
            ? Path.Combine(home, ".pi", "extensions")
            : Path.Combine(profileRoot, "extensions");
        var sessions = Path.Combine(global, "sessions");
        return new PiAgentPaths(
            home,
            fullCwd,
            global,
            project,
            Path.Combine(global, "settings.json"),
            Path.Combine(project, "settings.json"),
            Path.Combine(global, "auth.json"),
            Path.Combine(global, "models.json"),
            Path.Combine(global, "keybindings.json"),
            sessions,
            Path.Combine(sessions, EncodeCwd(fullCwd)),
            globalPiSharp,
            projectPiSharp,
            Path.Combine(globalPiSharp, "settings.json"),
            Path.Combine(projectPiSharp, "settings.json"),
            globalExtensions,
            profile);
    }

    public static string EncodeCwd(string cwd)
        => $"--{cwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-')}--";

    internal static string DefaultHomeDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile)) return userProfile;
        return Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? throw new InvalidOperationException("Could not resolve the current user's home directory.");
    }

    private static string? ResolveProfileRoot(string home, string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || string.Equals(profile, PiProfiles.DefaultProfileName, StringComparison.Ordinal))
            return null;
        return PiProfile.ResolveRootDirectory(profile, home);
    }
}
