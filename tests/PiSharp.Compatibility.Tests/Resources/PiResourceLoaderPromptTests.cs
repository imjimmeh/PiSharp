using PiSharp.Compatibility.Resources;
using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Tests.Resources;

public sealed class PiResourceLoaderPromptTests
{
    [Fact]
    public async Task LoadAsyncDiscoversProjectSystemAndAppendPromptFiles()
    {
        var root = CreateTempDir();
        var home = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "SYSTEM.md"), "SYSTEM BODY");
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "APPEND_SYSTEM.md"), "APPEND BODY");
        var settings = await new PiSettingsStore().LoadAsync(root, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(
            settings, root, [], [], [], [], false, false, false, false, false));

        Assert.Equal("SYSTEM BODY", resources.SystemPrompt);
        Assert.Equal(["APPEND BODY"], resources.AppendSystemPrompts);
    }

    [Fact]
    public async Task LoadAsyncLoadsGlobalThenAncestorContextFiles()
    {
        var root = CreateTempDir();
        var home = CreateTempDir();
        var child = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "root rules");
        await File.WriteAllTextAsync(Path.Combine(child, "CLAUDE.md"), "child rules");
        var settings = await new PiSettingsStore().LoadAsync(root, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(
            settings, child, [], [], [], [], false, false, false, false, false));

        Assert.Contains(resources.ContextFiles ?? [], file => file.Path.EndsWith("AGENTS.md") && file.Content == "root rules");
        Assert.Contains(resources.ContextFiles ?? [], file => file.Path.EndsWith("CLAUDE.md") && file.Content == "child rules");
        Assert.Equal((resources.ContextFiles ?? []).Count, (resources.ContextFiles ?? []).Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-prompt-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
