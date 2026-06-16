using PiSharp.Cli.Packages;
using PiSharp.Compatibility.Settings;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class PackageSettingsServiceTests
{
    private static async Task<(PiPackageSettingsService Service, string Root)> CreateSandboxAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent"));
        Directory.CreateDirectory(repo);

        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        var service = new PiPackageSettingsService(store, snapshot);

        return (service, root);
    }

    private static async Task<PiPackageSettingsService> CreateServiceWithFilesAsync(string root, string userSettings, string projectSettings)
    {
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var userDir = Path.Combine(home, ".pi", "agent");
        var projectDir = Path.Combine(repo, ".pi");

        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(userDir, "settings.json"), userSettings);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "settings.json"), projectSettings);

        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        return new PiPackageSettingsService(store, snapshot);
    }

    [Fact]
    public async Task InstallAddsSourceToUserSettingsByDefault()
    {
        var (service, root) = await CreateSandboxAsync();
        var rootPath = Path.Combine(root, "home", ".pi", "agent");

        await service.InstallAsync("npm:@foo/bar");

        var saved = await File.ReadAllTextAsync(Path.Combine(rootPath, "settings.json"));
        Assert.Contains("npm:@foo/bar", saved);
    }

    [Fact]
    public async Task InstallWithLocalFlagAddsSourceToProjectSettings()
    {
        var (service, root) = await CreateSandboxAsync();
        var repoPath = Path.Combine(root, "repo");

        await service.InstallAsync("npm:@foo/bar", local: true);

        var saved = await File.ReadAllTextAsync(Path.Combine(repoPath, ".pi", "settings.json"));
        Assert.Contains("npm:@foo/bar", saved);
    }

    [Fact]
    public async Task RemoveRemovesMatchingSourceByIdentity()
    {
        var (service, _) = await CreateSandboxAsync();

        await service.InstallAsync("npm:@foo/bar@1.2.3");
        var removed = await service.RemoveAsync("npm:@foo/bar@2.0.0");

        Assert.True(removed);
    }

    [Fact]
    public async Task RemoveWithLocalFlagOnlyMutatesProjectSettings()
    {
        var (service, root) = await CreateSandboxAsync();

        await service.InstallAsync("npm:@foo/bar");
        await service.RemoveAsync("npm:@foo/bar", local: true);

        var userSettings = await File.ReadAllTextAsync(Path.Combine(root, "home", ".pi", "agent", "settings.json"));
        var projectSettingsPath = Path.Combine(root, "repo", ".pi", "settings.json");

        Assert.Contains("npm:@foo/bar", userSettings);
        Assert.False(File.Exists(projectSettingsPath));
    }

    [Fact]
    public async Task RemoveReturnsFalseWhenNoMatchFound()
    {
        var (service, _) = await CreateSandboxAsync();

        var removed = await service.RemoveAsync("npm:nonexistent");

        Assert.False(removed);
    }

    [Fact]
    public async Task ListReturnsProjectPackageWinningOverUserPackageForSameIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pkg-settings-" + Guid.NewGuid().ToString("N"));
        var service = await CreateServiceWithFilesAsync(root,
            """{"packages":["npm:@foo/bar"]}""",
            """{"packages":["npm:@foo/bar"]}""");

        var result = await service.ListAsync();

        Assert.Single(result);
        Assert.Equal(PiSettingsLayer.ProjectLegacy, result[0].Layer);
    }

    [Fact]
    public async Task LocalRelativePathsPersistRelativeToSettingsFileBase()
    {
        var (service, root) = await CreateSandboxAsync();
        var repo = Path.Combine(root, "repo");

        await service.InstallAsync("./my-local-package", local: true);

        var saved = await File.ReadAllTextAsync(Path.Combine(repo, ".pi", "settings.json"));
        Assert.Contains("./my-local-package", saved);
    }
}


