using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Tests.Settings;

/// <summary>
/// Verifies the atomic temp-file-replace hardening of <c>SaveDocumentAsync</c> (via the public
/// <see cref="PiSettingsStore.SaveLayerAsync"/>): the target is written with indented JSON, a
/// trailing newline, no leftover temp files, and the directory is created on demand.
/// </summary>
public sealed class PiSettingsStoreAtomicWriteTests
{
    private static async Task<(PiSettingsStore Store, string Home, string Repo, PiSettingsSnapshot Snapshot)> CreateStoreAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-atomic-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        return (store, home, repo, snapshot);
    }

    [Fact]
    public async Task SaveLayerAsyncWritesIndentedJsonWithTrailingNewline()
    {
        var (store, home, _, snapshot) = await CreateStoreAsync();

        await store.SaveLayerAsync(snapshot, PiSettingsLayer.GlobalPiSharp, doc => doc.SetString("defaultProvider", "openai"));

        var json = await File.ReadAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"));
        Assert.EndsWith(Environment.NewLine, json);
        Assert.Contains("\"defaultProvider\": \"openai\"", json);
    }

    [Fact]
    public async Task RepeatedSavesLeaveNoTempFilesInDirectory()
    {
        var (store, home, _, snapshot) = await CreateStoreAsync();

        for (var i = 0; i < 5; i++)
        {
            await store.SaveLayerAsync(snapshot, PiSettingsLayer.GlobalPiSharp, doc => doc.SetString("defaultProvider", $"p{i}"));
        }

        var dir = Path.Combine(home, ".pi", "PiSharp");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "settings.json"));
        Assert.Contains("\"defaultProvider\": \"p4\"", json);
    }

    [Fact]
    public async Task SaveLayerAsyncCreatesMissingDirectory()
    {
        var (store, _, repo, snapshot) = await CreateStoreAsync();

        await store.SaveLayerAsync(snapshot, PiSettingsLayer.ProjectPiSharp, doc => doc.SetString("defaultProvider", "openai"));

        Assert.True(File.Exists(Path.Combine(repo, ".pi", "PiSharp", "settings.json")));
    }

    [Fact]
    public async Task SaveLayerAsyncPreservesExistingContentAcrossReloads()
    {
        var (store, _, _, snapshot) = await CreateStoreAsync();

        await store.SaveLayerAsync(snapshot, PiSettingsLayer.GlobalLegacy, doc => doc.SetString("defaultProvider", "first"));
        snapshot = await store.LoadAsync(snapshot.Paths.Cwd, snapshot.Paths.HomeDirectory);
        await store.SaveLayerAsync(snapshot, PiSettingsLayer.GlobalLegacy, doc => doc.SetBool("offline", true));

        var json = await File.ReadAllTextAsync(snapshot.Paths.GlobalSettingsPath);
        Assert.Contains("\"defaultProvider\": \"first\"", json);
        Assert.Contains("\"offline\": true", json);
    }
}
