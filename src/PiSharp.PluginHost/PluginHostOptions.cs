namespace PiSharp.PluginHost;

public sealed record PluginHostOptions(
    IReadOnlyList<string> PluginDirectories,
    IReadOnlyList<string>? ExplicitPluginPaths = null,
    bool HotReload = false)
{
    public static PluginHostOptions FromCwd(
        string cwd,
        IReadOnlyList<string>? explicitPluginPaths = null,
        string? homeDirectory = null)
    {
        var pluginDirectories = new List<string>
        {
            Path.Combine(cwd, "plugins"),
            Path.Combine(cwd, ".pi", "extensions"),
        };

        if (!string.IsNullOrWhiteSpace(homeDirectory))
            pluginDirectories.Add(Path.Combine(homeDirectory, ".pi", "extensions"));

        return new PluginHostOptions(pluginDirectories, explicitPluginPaths);
    }
}
