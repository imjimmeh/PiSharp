using PiSharp.Compatibility.Resources;
using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Tests.Resources;

public sealed class PiResourceLoaderTests
{
    [Fact]
    public async Task LoadAsyncCombinesSettingsCliAndPackageResourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var globalExt = Directory.CreateDirectory(Path.Combine(root, "global-ext")).FullName;
        var cliExt = Directory.CreateDirectory(Path.Combine(root, "cli-ext")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "pkg")).FullName;
        var packageExt = Directory.CreateDirectory(Path.Combine(package, "extensions")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), $"{{\"extensions\":[\"{globalExt.Replace("\\", "\\\\")}\"],\"packages\":[\"{package.Replace("\\", "\\\\")}\"]}}\n");
        var settings = await new PiSettingsStore().LoadAsync(repo, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, repo, [cliExt], [], [], [], false, false, false, false, false));

        Assert.Contains(globalExt, resources.ExtensionPaths);
        Assert.Contains(cliExt, resources.ExtensionPaths);
        Assert.Contains(packageExt, resources.ExtensionPaths);
        Assert.DoesNotContain(resources.Diagnostics, diagnostic => diagnostic.Code == "missing");
    }

    [Fact]
    public async Task PackageManifestResourcesOverrideConventionDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var package = Directory.CreateDirectory(Path.Combine(root, "pkg")).FullName;
        var manifestExt = Directory.CreateDirectory(Path.Combine(package, "dist")).FullName;
        var conventionExt = Directory.CreateDirectory(Path.Combine(package, "extensions")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"./dist\"]}}\n");
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), $"{{\"packages\":[\"{package.Replace("\\", "\\\\")}\"]}}\n");
        var settings = await new PiSettingsStore().LoadAsync(repo, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, repo, [], [], [], [], false, false, false, false, false));

        Assert.Contains(manifestExt, resources.ExtensionPaths);
        Assert.DoesNotContain(conventionExt, resources.ExtensionPaths);
    }

    [Fact]
    public async Task LoadAsyncDiscoversDefaultSkillLocations()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var child = Path.Combine(repo, "src", "feature");
        var projectPiSkills = Directory.CreateDirectory(Path.Combine(repo, ".pi", "skills")).FullName;
        var globalPiSkills = Directory.CreateDirectory(Path.Combine(home, ".pi", "agent", "skills")).FullName;
        var repoAgentsSkills = Directory.CreateDirectory(Path.Combine(repo, ".agents", "skills")).FullName;
        var childAgentsSkills = Directory.CreateDirectory(Path.Combine(repo, "src", ".agents", "skills")).FullName;
        Directory.CreateDirectory(child);
        var settings = await new PiSettingsStore().LoadAsync(child, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, child, [], [], [], [], false, false, false, false, false));

        Assert.Contains(projectPiSkills, resources.SkillPaths);
        Assert.Contains(globalPiSkills, resources.SkillPaths);
        Assert.Contains(repoAgentsSkills, resources.SkillPaths);
        Assert.Contains(childAgentsSkills, resources.SkillPaths);
    }

    [Fact]
    public async Task LoadAsyncDiscoversDefaultExtensionEntryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent", "extensions", "global-dir"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "extensions", "project-dir"));
        var projectFile = Path.Combine(repo, ".pi", "extensions", "project.ts");
        var projectJsFile = Path.Combine(repo, ".pi", "extensions", "project-js.js");
        var projectIndex = Path.Combine(repo, ".pi", "extensions", "project-dir", "index.ts");
        var projectJsIndex = Path.Combine(repo, ".pi", "extensions", "project-dir", "index.js");
        var globalFile = Path.Combine(home, ".pi", "agent", "extensions", "global.ts");
        var globalJsFile = Path.Combine(home, ".pi", "agent", "extensions", "global-js.js");
        var globalIndex = Path.Combine(home, ".pi", "agent", "extensions", "global-dir", "index.ts");
        var globalJsIndex = Path.Combine(home, ".pi", "agent", "extensions", "global-dir", "index.js");
        await File.WriteAllTextAsync(projectFile, "export default {};");
        await File.WriteAllTextAsync(projectJsFile, "export default {};");
        await File.WriteAllTextAsync(projectIndex, "export default {};");
        await File.WriteAllTextAsync(projectJsIndex, "export default {};");
        await File.WriteAllTextAsync(globalFile, "export default {};");
        await File.WriteAllTextAsync(globalJsFile, "export default {};");
        await File.WriteAllTextAsync(globalIndex, "export default {};");
        await File.WriteAllTextAsync(globalJsIndex, "export default {};");
        var settings = await new PiSettingsStore().LoadAsync(repo, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, repo, [], [], [], [], false, false, false, false, false));

        Assert.Contains(projectFile, resources.ExtensionPaths);
        Assert.Contains(projectJsFile, resources.ExtensionPaths);
        Assert.Contains(projectIndex, resources.ExtensionPaths);
        Assert.Contains(projectJsIndex, resources.ExtensionPaths);
        Assert.Contains(globalFile, resources.ExtensionPaths);
        Assert.Contains(globalJsFile, resources.ExtensionPaths);
        Assert.Contains(globalIndex, resources.ExtensionPaths);
        Assert.Contains(globalJsIndex, resources.ExtensionPaths);
    }

    [Fact]
    public async Task NoSkillsSuppressesDefaultSettingsAndPackageSkillsButKeepsCliSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var cliSkill = Directory.CreateDirectory(Path.Combine(root, "cli-skill")).FullName;
        var settingsSkill = Directory.CreateDirectory(Path.Combine(root, "settings-skill")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "pkg")).FullName;
        var packageSkill = Directory.CreateDirectory(Path.Combine(package, "skills")).FullName;
        var defaultSkill = Directory.CreateDirectory(Path.Combine(repo, ".pi", "skills")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), $"{{\"skills\":[\"{settingsSkill.Replace("\\", "\\\\")}\"],\"packages\":[\"{package.Replace("\\", "\\\\")}\"]}}\n");
        var settings = await new PiSettingsStore().LoadAsync(repo, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, repo, [], [cliSkill], [], [], false, true, false, false, false));

        Assert.Equal([cliSkill], resources.SkillPaths);
        Assert.DoesNotContain(defaultSkill, resources.SkillPaths);
        Assert.DoesNotContain(settingsSkill, resources.SkillPaths);
        Assert.DoesNotContain(packageSkill, resources.SkillPaths);
    }

    [Fact]
    public async Task DisableFlagsSuppressMatchingResourceCategories()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ext = Directory.CreateDirectory(Path.Combine(root, "ext")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var defaultExt = Path.Combine(root, ".pi", "extensions", "default.ts");
        await File.WriteAllTextAsync(defaultExt, "export default {};");
        var settings = await new PiSettingsStore().LoadAsync(root, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, root, [ext], [], [], [], true, false, false, false, false));

        Assert.Empty(resources.ExtensionPaths);
    }

    [Fact]
    public async Task MissingPackageReferencesProduceWarnings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), "{\"packages\":[\"missing-package\"]}\n");
        var settings = await new PiSettingsStore().LoadAsync(root, home);

        var resources = await new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(settings, root, [], [], [], [], false, false, false, false, false));

        Assert.Contains(resources.Diagnostics, diagnostic => diagnostic.Type == "package" && diagnostic.Code == "missing");
    }
}
