using System.Text.Json;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Runtime.Tests;

public sealed class ExtensionSettingsServiceTests
{
    private static async Task<(ExtensionSettingsService Service, string Home, string Repo)> CreateAsync(
        Dictionary<string, object?>? globalPiSharp = null,
        Dictionary<string, object?>? projectPiSharp = null,
        Dictionary<string, object?>? globalLegacy = null,
        ExtensionRegistry? registry = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-svc-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repo = Path.Combine(root, "repo");
        if (globalPiSharp is not null) await WriteSettingsAsync(home, "PiSharp", globalPiSharp);
        if (projectPiSharp is not null) await WriteSettingsAsync(repo, "PiSharp", projectPiSharp);
        if (globalLegacy is not null) await WriteSettingsAsync(home, "agent", globalLegacy);

        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repo, home);
        var service = new ExtensionSettingsService(store, snapshot, registry);
        return (service, home, repo);
    }

    private static Task WriteSettingsAsync(string baseDir, string subdir, Dictionary<string, object?> contents)
    {
        var dir = Path.Combine(baseDir, ".pi", subdir);
        Directory.CreateDirectory(dir);
        return File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(contents));
    }

    [Fact]
    public async Task GetRawReadsMergedValue()
    {
        var (service, _, _) = await CreateAsync(globalPiSharp: new() { ["extensions"] = new Dictionary<string, object?> { ["ns"] = new Dictionary<string, object?> { ["backend"] = "sqlite" } } });
        Assert.Equal("sqlite", service.GetRaw("extensions.ns.backend"));
    }

    [Fact]
    public async Task SourceScopeWritesExtensionsToGlobalPiSharpWhenNoProvenance()
    {
        var (service, home, _) = await CreateAsync();
        await service.SetRawAsync("extensions.my-ext.backend", "sqlite", ExtensionSettingsScope.Source, "extension:my-ext");

        var json = await File.ReadAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"));
        Assert.Contains("my-ext", json);
        Assert.Equal("sqlite", service.GetRaw("extensions.my-ext.backend"));
    }

    [Fact]
    public async Task GlobalAndProjectScopesPinLayers()
    {
        var (service, _, _) = await CreateAsync();
        await service.SetRawAsync("extensions.ns.k", "global", ExtensionSettingsScope.Global, "extension:ns");
        await service.SetRawAsync("extensions.ns.k", "project", ExtensionSettingsScope.Project, "extension:ns");

        // Project wins the merged read, but both layers hold the value independently.
        Assert.Equal("project", service.GetRaw("extensions.ns.k"));
    }

    [Fact]
    public async Task SourceScopeMapsLegacyProvenanceToPiSharpSiblingForExtensionSettings()
    {
        // The whole extensions container resolves from the legacy global layer; extension writes
        // must land in ~/.pi/PiSharp, never ~/.pi/agent.
        var (service, home, _) = await CreateAsync(globalLegacy: new() { ["extensions"] = new Dictionary<string, object?> { ["ns"] = "seed" } });

        await service.SetRawAsync("extensions.ns.backend", "sqlite", ExtensionSettingsScope.Source, "extension:ns");

        Assert.True(File.Exists(Path.Combine(home, ".pi", "PiSharp", "settings.json")));
        var legacyGlobalPath = Path.Combine(home, ".pi", "agent", "settings.json");
        var legacy = await File.ReadAllTextAsync(legacyGlobalPath);
        Assert.DoesNotContain("\"backend\"", legacy);
        Assert.Equal("sqlite", service.GetRaw("extensions.ns.backend"));
    }

    [Fact]
    public async Task CoreKeyDefaultsToLegacyGlobalWhenNoLayerWins()
    {
        var (service, home, _) = await CreateAsync();
        await service.SetRawAsync("defaultProvider", "openai", ExtensionSettingsScope.Source, "runtime:model");

        var legacyGlobal = await File.ReadAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"));
        Assert.Contains("\"defaultProvider\": \"openai\"", legacyGlobal);
    }

    [Fact]
    public async Task SetRawOnLayerAsyncPinsExactLayer()
    {
        var (service, home, _) = await CreateAsync();
        await service.SetRawOnLayerAsync("defaultModel", "gpt-4o", PiSettingsLayer.ProjectPiSharp, "runtime:model");

        var project = await File.ReadAllTextAsync(Path.Combine(home, "..", "repo", ".pi", "PiSharp", "settings.json"));
        Assert.Contains("gpt-4o", project);
    }

    [Fact]
    public async Task SettingsChangedEventPublishedToRegistryHandlers()
    {
        var registry = new ExtensionRegistry();
        var (service, _, _) = await CreateAsync(registry: registry);

        ExtensionSettingsChange? observed = null;
        using (registry.RegisterHandler("extension:observer", ExtensionEventNames.SettingsChanged, (evt, _) =>
        {
            observed = evt.Payload as ExtensionSettingsChange;
            return Task.CompletedTask;
        }))
        {
            await service.SetRawAsync("extensions.ns.k", "v", ExtensionSettingsScope.Source, "extension:ns");
        }

        Assert.NotNull(observed);
        Assert.Equal("extensions.ns.k", observed!.Key);
        Assert.Equal("extension:ns", observed.SourceId);
        Assert.Equal("GlobalPiSharp", observed.Layer);
    }

    [Fact]
    public async Task OnChangeSubscriptionReceivesEveryCommittedWrite()
    {
        var (service, _, _) = await CreateAsync();
        var keys = new List<string>();
        using (service.OnChange(change => keys.Add(change.Key)))
        {
            await service.SetRawAsync("extensions.ns.a", 1, ExtensionSettingsScope.Source, "extension:ns");
            await service.SetRawAsync("extensions.ns.b", 2, ExtensionSettingsScope.Source, "extension:ns");
        }
        Assert.Equal(["extensions.ns.a", "extensions.ns.b"], keys);
    }

    [Fact]
    public async Task TryClaimNamespaceIsFirstWriterWins()
    {
        var (service, _, _) = await CreateAsync();
        Assert.True(service.TryClaimNamespace("ns", "extension:a"));
        Assert.False(service.TryClaimNamespace("ns", "extension:b"));
    }
}
