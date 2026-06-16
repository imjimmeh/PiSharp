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
    string ProjectPiSharpSettingsPath)
{
    public static PiAgentPaths FromCwd(string cwd, string? homeDirectory = null)
    {
        var fullCwd = Path.GetFullPath(cwd);
        var home = Path.GetFullPath(string.IsNullOrWhiteSpace(homeDirectory) ? DefaultHomeDirectory() : homeDirectory);
        var global = Path.Combine(home, ".pi", "agent");
        var project = Path.Combine(fullCwd, ".pi");
        var globalPiSharp = Path.Combine(home, ".pi", "PiSharp");
        var projectPiSharp = Path.Combine(project, "PiSharp");
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
            Path.Combine(projectPiSharp, "settings.json"));
    }

    public static string EncodeCwd(string cwd)
        => $"--{cwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-')}--";

    private static string DefaultHomeDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile)) return userProfile;
        return Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? throw new InvalidOperationException("Could not resolve the current user's home directory.");
    }
}
