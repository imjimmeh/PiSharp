using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;
using PiSharp.Packages;
using Xunit;

namespace PiSharp.Runtime.Tests.Packages;

/// <summary>
/// P04 (GAP-55): RuntimePackageService adapts the shared package engine
/// (<see cref="IPackageCommandRunner"/>) to <see cref="IExtensionPackageApi"/>,
/// triggers a hot reload after install/update/remove, and emits
/// <c>extensions_changed</c>.
/// </summary>
public sealed class RuntimePackageServiceTests
{
    [Fact]
    public async Task InstallAsyncRunsInstallThenReloadsAndEmitsExtensionsChanged()
    {
        var runner = new FakePackageCommandRunner();
        var events = new List<(string Name, object? Payload)>();
        var reloads = 0;
        var service = CreateService(runner, () => reloads++, events);

        var result = await service.InstallAsync("pi-package@1.0.0", local: false, force: true, offline: true);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(("pi-package@1.0.0", false, true, true), runner.LastInstall);
        Assert.Equal(1, reloads);
        var (name, payload) = Assert.Single(events);
        Assert.Equal(ExtensionEventNames.PackagesChanged, name);
        Assert.Equal(["pi-package@1.0.0"], ReadList(payload, "added"));
        Assert.Empty(ReadList(payload, "removed"));
        Assert.Empty(ReadList(payload, "updated"));
    }

    [Fact]
    public async Task UpdateAsyncMapsExtensionPackageUpdateRequestToPackageUpdateRequest()
    {
        var runner = new FakePackageCommandRunner();
        var events = new List<(string Name, object? Payload)>();
        var reloads = 0;
        var service = CreateService(runner, () => reloads++, events);

        var result = await service.UpdateAsync(new ExtensionPackageUpdateRequest(
            Source: "pi-package", Extensions: true, ExtensionSource: "pi-other", Force: true, Offline: true));

        Assert.True(result.Success);
        Assert.NotNull(runner.LastUpdate);
        Assert.Equal("pi-package", runner.LastUpdate!.Source);
        Assert.False(runner.LastUpdate.Self);
        Assert.True(runner.LastUpdate.Extensions);
        Assert.Equal("pi-other", runner.LastUpdate.ExtensionSource);
        Assert.True(runner.LastUpdate.Force);
        Assert.True(runner.LastUpdate.Offline);
        Assert.Equal(1, reloads);
        var (name, payload) = Assert.Single(events);
        Assert.Equal(ExtensionEventNames.PackagesChanged, name);
        Assert.Equal(["pi-package"], ReadList(payload, "updated"));
    }

    [Fact]
    public async Task RemoveAsyncRunsRemoveThenReloadsAndEmitsExtensionsChanged()
    {
        var runner = new FakePackageCommandRunner { RemoveResult = true };
        var events = new List<(string Name, object? Payload)>();
        var reloads = 0;
        var service = CreateService(runner, () => reloads++, events);

        var removed = await service.RemoveAsync("pi-package", local: true);

        Assert.True(removed);
        Assert.Equal(("pi-package", true), runner.LastRemove);
        Assert.Equal(1, reloads);
        var (name, payload) = Assert.Single(events);
        Assert.Equal(ExtensionEventNames.PackagesChanged, name);
        Assert.Equal(["pi-package"], ReadList(payload, "removed"));
    }

    [Fact]
    public async Task ListAsyncMapsPackageListEntriesToExtensionInstalledPackages()
    {
        var runner = new FakePackageCommandRunner
        {
            ListResult =
            [
                new PackageListEntry("pi-package", PiSettingsLayer.ProjectPiSharp),
                new PackageListEntry("pi-global", PiSettingsLayer.GlobalPiSharp)
            ]
        };
        var service = CreateService(runner, () => { }, []);

        var installed = await service.ListAsync();

        Assert.Collection(installed,
            entry => { Assert.Equal("pi-package", entry.Source); Assert.Equal(PiSettingsLayer.ProjectPiSharp.ToString(), entry.Layer); },
            entry => { Assert.Equal("pi-global", entry.Source); Assert.Equal(PiSettingsLayer.GlobalPiSharp.ToString(), entry.Layer); });
    }

    [Fact]
    public async Task InstallFailureReturnsFailedResultWithoutReloading()
    {
        var runner = new FakePackageCommandRunner { InstallException = new InvalidOperationException("boom") };
        var events = new List<(string Name, object? Payload)>();
        var reloads = 0;
        var service = CreateService(runner, () => reloads++, events);

        var result = await service.InstallAsync("pi-package");

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error);
        Assert.Equal(0, reloads);
        Assert.Empty(events);
    }

    [Fact]
    public async Task RemoveFailureReturnsFalseWithoutReloading()
    {
        var runner = new FakePackageCommandRunner { RemoveResult = false };
        var events = new List<(string Name, object? Payload)>();
        var reloads = 0;
        var service = CreateService(runner, () => reloads++, events);

        var removed = await service.RemoveAsync("pi-package");

        Assert.False(removed);
        Assert.Equal(0, reloads);
        Assert.Empty(events);
    }

    private static RuntimePackageService CreateService(
        IPackageCommandRunner runner,
        Action onReload,
        List<(string Name, object? Payload)> events)
        => new(
            runnerFactory: _ => Task.FromResult(runner),
            reloadExtensionsAsync: _ => { onReload(); return Task.CompletedTask; },
            emitEventAsync: (name, payload, _) => { events.Add((name, payload)); return Task.CompletedTask; });

    private static IReadOnlyList<string> ReadList(object? payload, string property)
    {
        using var document = System.Text.Json.JsonSerializer.SerializeToDocument(payload);
        return document.RootElement.GetProperty(property).EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private sealed class FakePackageCommandRunner : IPackageCommandRunner
    {
        public (string Source, bool Local, bool Force, bool Offline)? LastInstall { get; private set; }
        public (string Source, bool Local)? LastRemove { get; private set; }
        public PackageUpdateRequest? LastUpdate { get; private set; }
        public bool RemoveResult { get; set; }
        public List<PackageListEntry> ListResult { get; set; } = [];
        public Exception? InstallException { get; set; }

        public Task InstallAsync(string source, bool local, bool force = false, bool offline = false)
        {
            if (InstallException is not null) throw InstallException;
            LastInstall = (source, local, force, offline);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string source, bool local)
        {
            LastRemove = (source, local);
            return Task.FromResult(RemoveResult);
        }

        public Task<List<PackageListEntry>> ListAsync() => Task.FromResult(ListResult);

        public Task ConfigAsync() => Task.CompletedTask;

        public Task UpdateAsync(PackageUpdateRequest request)
        {
            LastUpdate = request;
            return Task.CompletedTask;
        }
    }
}
