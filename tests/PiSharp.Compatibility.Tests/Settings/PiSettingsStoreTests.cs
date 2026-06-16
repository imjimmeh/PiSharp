using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Tests.Settings;

public sealed class PiSettingsStoreTests
{
    [Fact]
    public async Task LoadAsyncMergesGlobalAndProjectSettingsUsingTypeScriptPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), "{\"defaultProvider\":\"global\",\"defaultModel\":\"a\",\"extensions\":[\"global-ext\"],\"nested\":{\"keep\":true}}\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "settings.json"), "{\"defaultModel\":\"project\",\"skills\":[\"project-skill\"],\"nested\":{\"project\":true}}\n");

        var snapshot = await new PiSettingsStore().LoadAsync(repo, home);

        Assert.Equal(Path.Combine(home, ".pi", "agent", "settings.json"), snapshot.Paths.GlobalSettingsPath);
        Assert.Equal(Path.Combine(repo, ".pi", "settings.json"), snapshot.Paths.ProjectSettingsPath);
        Assert.Equal(Path.Combine(home, ".pi", "PiSharp", "settings.json"), snapshot.Paths.GlobalPiSharpSettingsPath);
        Assert.Equal(Path.Combine(repo, ".pi", "PiSharp", "settings.json"), snapshot.Paths.ProjectPiSharpSettingsPath);
        Assert.Equal("global", snapshot.Settings.DefaultProvider);
        Assert.Equal("project", snapshot.Settings.DefaultModel);
        Assert.Equal(["global-ext"], snapshot.Settings.Extensions);
        Assert.Equal(["project-skill"], snapshot.Settings.Skills);
        Assert.Equal(PiSettingsLayer.ProjectLegacy, snapshot.SourceLayerFor("defaultModel"));
        Assert.Equal(Path.Combine(snapshot.Paths.SessionsRoot, PiAgentPaths.EncodeCwd(Path.GetFullPath(repo))), snapshot.Paths.SessionDirectory);
    }

    [Fact]
    public async Task LoadAsyncAppliesPiSharpOverlayPrecedenceAndAppendKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), "{\"defaultProvider\":\"legacy-global\",\"defaultModel\":\"legacy-global\",\"sessionDir\":\"legacy-sessions\",\"extensions\":[\"legacy-ext\"],\"packages\":[\"legacy-package\"]}\n");
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"), "{\"defaultProvider\":\"pisharp-global\",\"sessionDir\":\"pisharp-sessions\",\"pisharp\":{\"append\":{\"extensions\":[\"global-pisharp-ext\"],\"packages\":[\"global-package\"]}}}\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "settings.json"), "{\"defaultProvider\":\"legacy-project\",\"defaultModel\":\"legacy-project\",\"skills\":[\"project-skill\"]}\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "PiSharp", "settings.json"), "{\"defaultModel\":\"pisharp-project\",\"pisharp\":{\"append\":{\"extensions\":[\"project-pisharp-ext\"],\"packages\":[\"project-package\"]}}}\n");

        var snapshot = await new PiSettingsStore().LoadAsync(repo, home);

        Assert.Equal("legacy-project", snapshot.Settings.DefaultProvider);
        Assert.Equal("pisharp-project", snapshot.Settings.DefaultModel);
        Assert.Equal("pisharp-sessions", snapshot.Settings.SessionDir);
        Assert.Equal(["legacy-ext", "global-pisharp-ext", "project-pisharp-ext"], snapshot.Settings.Extensions);
        Assert.Equal(["legacy-package", "global-package", "project-package"], snapshot.Settings.Packages);
        Assert.Equal(["project-skill"], snapshot.Settings.Skills);
        Assert.Equal(["legacy-ext"], snapshot.Global.Settings.Extensions);
        Assert.Equal(["legacy-package"], snapshot.Global.Settings.Packages);
        Assert.Empty(snapshot.GlobalPiSharpOrEmpty.Settings.Extensions);
        Assert.Empty(snapshot.ProjectPiSharpOrEmpty.Settings.Extensions);
        Assert.Empty(snapshot.GlobalPiSharpOrEmpty.Settings.Packages);
        Assert.Empty(snapshot.ProjectPiSharpOrEmpty.Settings.Packages);
        Assert.Equal(PiSettingsLayer.ProjectLegacy, snapshot.SourceLayerFor("defaultProvider"));
        Assert.Equal(PiSettingsLayer.ProjectPiSharp, snapshot.SourceLayerFor("defaultModel"));
        Assert.Equal(PiSettingsLayer.GlobalPiSharp, snapshot.SourceLayerFor("sessionDir"));
    }

    [Fact]
    public async Task SaveGlobalAsyncPreservesUnknownFieldsWhenUpdatingKnownSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);
        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{\"defaultModel\":\"old\",\"external\":{\"enabled\":true}}\n");
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);

        await store.SaveGlobalAsync(snapshot, document => document.SetString("defaultModel", "new"));
        var saved = await File.ReadAllTextAsync(settingsPath);

        Assert.Contains("\"defaultModel\": \"new\"", saved);
        Assert.Contains("\"external\"", saved);
        Assert.Contains("\"enabled\": true", saved);
    }

    [Fact]
    public async Task ConfigurationLoadsAllSettingsLayersInPiSharpPrecedenceOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-config-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));

        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), """
            { "defaultModel": "global-legacy", "logging": { "level": "Debug" } }
            """);
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"), """
            { "defaultModel": "global-pisharp", "logging": { "level": "Information" } }
            """);
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "settings.json"), """
            { "defaultModel": "project-legacy", "logging": { "level": "Warning" } }
            """);
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "PiSharp", "settings.json"), """
            { "defaultModel": "project-pisharp", "logging": { "level": "Error" } }
            """);

        var paths = PiAgentPaths.FromCwd(repo, home);
        var configuration = PiSettingsConfiguration.Build(paths);

        Assert.Equal("project-pisharp", configuration["defaultModel"]);
        Assert.Equal("Error", configuration["logging:level"]);
    }

    [Fact]
    public async Task LoggingSettingsReadFromProjectPiSharpOverrideGlobalPiSharpSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-logging-config-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));
        var globalLog = Path.Combine(root, "global.log");
        var projectLog = Path.Combine(root, "project.log");
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"),
            $@"{{ ""logging"": {{ ""file"": ""{globalLog.Replace("\\", "\\\\")}"", ""level"": ""Information"", ""maxFiles"": 3 }} }}");
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "PiSharp", "settings.json"),
            $@"{{ ""logging"": {{ ""file"": ""{projectLog.Replace("\\", "\\\\")}"", ""level"": ""Error"", ""maxFiles"": 5 }} }}");

        var configuration = PiSettingsConfiguration.Build(PiAgentPaths.FromCwd(repo, home));
        var logging = PiLoggingSettings.FromConfiguration(configuration);

        Assert.Equal(projectLog, logging.File);
        Assert.Equal("Error", logging.Level);
        Assert.Equal(5, logging.MaxFiles);
    }

    [Fact]
    public async Task LoadAsyncUsesConfigurationForScalarsButPreservesAppendArraySemantics()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-store-config-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repo, ".pi", "PiSharp"));

        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"), """
            { "defaultProvider": "global", "extensions": ["legacy-ext"] }
            """);
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"), """
            { "defaultProvider": "global-pisharp", "pisharp": { "append": { "extensions": ["global-pisharp-ext"] } } }
            """);
        await File.WriteAllTextAsync(Path.Combine(repo, ".pi", "PiSharp", "settings.json"), """
            { "defaultProvider": "project-pisharp", "pisharp": { "append": { "extensions": ["project-pisharp-ext"] } } }
            """);

        var snapshot = await new PiSettingsStore().LoadAsync(repo, home);

        Assert.Equal("project-pisharp", snapshot.Settings.DefaultProvider);
        Assert.Equal(["legacy-ext", "global-pisharp-ext", "project-pisharp-ext"], snapshot.Settings.Extensions);
        Assert.Equal(PiSettingsLayer.ProjectPiSharp, snapshot.SourceLayerFor("defaultProvider"));
        Assert.Equal(PiSettingsLayer.ProjectPiSharp, snapshot.SourceLayerFor("extensions"));
    }

    [Fact]
    public async Task SaveLayerAsyncWritesPiSharpOverlayDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);

        await store.SaveLayerAsync(snapshot, PiSettingsLayer.ProjectPiSharp, document => document.SetString("defaultModel", "overlay"));
        var saved = await File.ReadAllTextAsync(Path.Combine(repo, ".pi", "PiSharp", "settings.json"));

        Assert.Contains("\"defaultModel\": \"overlay\"", saved);
    }
}
