using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Tests for the per-extension settings/state wrappers: namespace prefixing, key validation,
/// first-writer-wins namespace claiming, and change-notification filtering. Backed by an
/// in-memory <see cref="IExtensionRuntimeSettings"/> and a file-backed store.
/// </summary>
public sealed class ExtensionScopedApiTests
{
    private static ExtensionDescriptor Descriptor(string id = "My-Ext") => new(id, id, "1.0");

    private sealed class FakeSettingsRuntime : IExtensionRuntimeSettings, IExtensionRuntimeSettingsClaimer
    {
        private readonly List<Action<ExtensionSettingsChange>> _subscribers = [];
        private readonly Dictionary<string, string> _claims = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public object? GetRaw(string path) => Values.TryGetValue(path, out var value) ? value : null;

        public Task SetRawAsync(string path, object? value, ExtensionSettingsScope scope, CancellationToken cancellationToken = default)
        {
            if (value is null) Values.Remove(path);
            else Values[path] = value;
            Fire(new ExtensionSettingsChange(path, value, scope.ToString(), "test"));
            return Task.CompletedTask;
        }

        public Task RemoveRawAsync(string path, ExtensionSettingsScope scope, CancellationToken cancellationToken = default)
            => SetRawAsync(path, null, scope, cancellationToken);

        public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
        {
            lock (_subscribers) _subscribers.Add(handler);
            return new FakeSub(() => { lock (_subscribers) _subscribers.Remove(handler); });
        }

        public Task<string?> SourceLayerForAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("GlobalPiSharp");

        public bool TryClaimNamespace(string extensionNamespace, string sourceId)
            => _claims.TryAdd(extensionNamespace, sourceId);

        public void Fire(ExtensionSettingsChange change)
        {
            Action<ExtensionSettingsChange>[] snapshot;
            lock (_subscribers) snapshot = _subscribers.ToArray();
            foreach (var subscriber in snapshot) subscriber(change);
        }
    }

    private sealed class FakeSub(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }

    [Fact]
    public async Task ScopedSetPrefixesNamespaceUnderExtensionsRoot()
    {
        var runtime = new FakeSettingsRuntime();
        var api = new ExtensionScopedSettings(Descriptor(), runtime);

        await api.SetAsync("backend", "sqlite");

        Assert.Equal("sqlite", runtime.Values["extensions.my-ext.backend"]);
        Assert.Equal("sqlite", api.Get("backend"));
    }

    [Fact]
    public async Task ScopedSetNullRemovesTheKey()
    {
        var runtime = new FakeSettingsRuntime();
        var api = new ExtensionScopedSettings(Descriptor(), runtime);
        await api.SetAsync("backend", "sqlite");
        await api.SetAsync("backend", null);

        Assert.False(runtime.Values.ContainsKey("extensions.my-ext.backend"));
        Assert.Null(api.Get("backend"));
    }

    [Fact]
    public async Task GetCoreReadsTopLevelPaths()
    {
        var runtime = new FakeSettingsRuntime();
        runtime.Values["logging.file"] = "app.log";
        var api = new ExtensionScopedSettings(Descriptor(), runtime);

        Assert.Equal("app.log", api.GetCore("logging.file"));
        Assert.Null(api.GetCore("missing"));
    }

    [Fact]
    public async Task InvalidSettingsKeyThrows()
    {
        var api = new ExtensionScopedSettings(Descriptor(), new FakeSettingsRuntime());
        await Assert.ThrowsAsync<ArgumentException>(() => api.SetAsync("bad key!", "v"));
        Assert.Throws<ArgumentException>(() => api.Get("bad key!"));
        await Assert.ThrowsAsync<ArgumentException>(() => api.SetAsync("", "v"));
    }

    [Fact]
    public async Task NamespaceCollisionRejectsSecondWriter()
    {
        var runtime = new FakeSettingsRuntime();
        var first = new ExtensionScopedSettings(Descriptor("A"), runtime);
        var second = new ExtensionScopedSettings(Descriptor("A"), runtime); // same id => same namespace

        await first.SetAsync("k", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SetAsync("k", 2));
    }

    [Fact]
    public async Task OnChangePrefixFiltersOnlyOwnNamespaceChanges()
    {
        var runtime = new FakeSettingsRuntime();
        var api = new ExtensionScopedSettings(Descriptor("My-Ext"), runtime);
        var receivedKeys = new List<string>();
        using (api.OnChange("backend", change => receivedKeys.Add(change.Key.Split('.').Last())))

        {
            await api.SetAsync("backend", "x");
            runtime.Fire(new ExtensionSettingsChange("extensions.other.key", 1, "GlobalPiSharp", "other"));
            await api.SetAsync("other", "y");
        }

        Assert.Equal(["backend"], receivedKeys);
    }

    [Fact]
    public async Task ScopedStateUsesFileBackedStorePerNamespace()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-scoped-state-" + Guid.NewGuid().ToString("N"));
        var userRoot = Path.Combine(root, "user");
        var projectRoot = Path.Combine(root, "project");
        var stateService = new TestStateRuntime(userRoot, projectRoot);
        var api = new ExtensionScopedState(Descriptor("My-Ext"), stateService);

        await api.SetAsync("goal", "build", ExtensionStateScope.User);
        Assert.Equal("build", await api.GetAsync("goal"));

        // User and Project are isolated
        Assert.Null(await api.GetAsync("goal", ExtensionStateScope.Project));
        await api.SetAsync("goal", "ship", ExtensionStateScope.Project);
        Assert.Equal("ship", await api.GetAsync("goal", ExtensionStateScope.Project));

        // File landed under the expected namespace directory
        Assert.True(File.Exists(Path.Combine(userRoot, "my-ext", "state.json")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "my-ext", "state.json")));
    }

    private sealed class TestStateRuntime(string userRoot, string projectRoot) : IExtensionRuntimeState
    {
        private readonly Dictionary<(string, ExtensionStateScope), IExtensionStateStore> _stores = [];
        public IExtensionStateStore GetStore(string extensionNamespace, ExtensionStateScope scope)
        {
            var root = scope == ExtensionStateScope.User ? Path.Combine(userRoot, extensionNamespace) : Path.Combine(projectRoot, extensionNamespace);
            if (!_stores.TryGetValue((extensionNamespace, scope), out var store))
            {
                store = new ExtensionStateStore(extensionNamespace, scope, root);
                _stores[(extensionNamespace, scope)] = store;
            }
            return store;
        }
    }
}
