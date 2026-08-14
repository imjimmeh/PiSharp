using System.Text.Json.Nodes;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class ExtensionStateStoreTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "pisharp-state-" + Guid.NewGuid().ToString("N"));

    private static ExtensionStateStore Create(string extensionNamespace = "pisharp-memory", ExtensionStateScope scope = ExtensionStateScope.User)
    {
        var root = NewRoot();
        return new ExtensionStateStore(extensionNamespace, scope, Path.Combine(root, extensionNamespace));
    }

    [Fact]
    public async Task SetAndGetRoundtripPersistsVersionedJson()
    {
        var store = Create();
        await store.SetAsync("backend", "sqlite");

        Assert.Equal("sqlite", await store.GetAsync("backend"));

        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.Contains("\"schemaVersion\": 0", json);
        Assert.Contains("\"data\"", json);
    }

    [Fact]
    public async Task SetNullRemovesKey()
    {
        var store = Create();
        await store.SetAsync("k", "v");
        await store.SetAsync("k", null);

        Assert.Null(await store.GetAsync("k"));
        Assert.Empty(await store.ListKeysAsync());
    }

    [Fact]
    public async Task SchemaVersionDefaultsToZeroAndSetPersists()
    {
        var store = Create();
        Assert.Equal(0, await store.GetSchemaVersionAsync());
        Assert.Equal(4, await store.SetSchemaVersionAsync(4));
        Assert.Equal(4, await store.GetSchemaVersionAsync());

        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.Contains("\"schemaVersion\": 4", json);
    }

    [Fact]
    public async Task MigrationChainRunsInOrderOnFirstLoadAndPersists()
    {
        var root = NewRoot();
        var ns = "ns";
        var dir = Path.Combine(root, ns);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "state.json"), "{\"schemaVersion\":0,\"data\":{\"seed\":1}}");

        var store = new ExtensionStateStore(ns, ExtensionStateScope.User, dir);
        await store.RegisterMigrationAsync(0, 1, (data, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>(data) { ["bootstrapped"] = true }));
        await store.RegisterMigrationAsync(1, 2, (data, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>(data) { ["final"] = true }));

        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(1L, await store.GetAsync("seed"));
        Assert.Equal(true, await store.GetAsync("bootstrapped"));
        Assert.Equal(true, await store.GetAsync("final"));

        var persisted = await File.ReadAllTextAsync(Path.Combine(dir, "state.json"));
        Assert.Contains("\"schemaVersion\": 2", persisted);
    }

    [Fact]
    public async Task MigrationGapThrowsWhenFileAtUncoveredVersion()
    {
        var root = NewRoot();
        var ns = "ns";
        var dir = Path.Combine(root, ns);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "state.json"), "{\"schemaVersion\":3,\"data\":{\"k\":1}}");

        var store = new ExtensionStateStore(ns, ExtensionStateScope.User, dir);
        await store.RegisterMigrationAsync(0, 1, (data, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(data));
        await store.RegisterMigrationAsync(1, 2, (data, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(data));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("k"));
    }

    [Fact]
    public async Task FreshStoreWithLaterBaselineMigrationDoesNotError()
    {
        var store = Create();
        await store.RegisterMigrationAsync(2, 3, (data, _) => Task.FromResult<IReadOnlyDictionary<string, object?>>(data));
        Assert.Equal(0, await store.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task GetTypedDeserializesJsonValues()
    {
        var store = Create();
        await store.SetAsync("count", 5);
        await store.SetAsync("tags", new[] { "a", "b" });

        Assert.Equal(5, await store.GetAsync<int>("count"));
        Assert.Equal(new[] { "a", "b" }, await store.GetAsync<string[]>("tags"));
    }

    [Fact]
    public async Task GetAllListKeysAndClear()
    {
        var store = Create();
        await store.SetAsync("a", 1);
        await store.SetAsync("b", "x");

        Assert.Equal(2, (await store.GetAllAsync()).Count);
        Assert.Equal("x", (await store.GetAllAsync())["b"]);
        Assert.Equal(new[] { "a", "b" }, (await store.ListKeysAsync()).OrderBy(k => k).ToArray());

        await store.ClearAsync();
        Assert.Empty(await store.ListKeysAsync());
        Assert.Equal(0, await store.GetSchemaVersionAsync()); // clear keeps version
    }

    [Fact]
    public async Task WritesLeaveNoTempFilesBehind()
    {
        var store = Create();
        for (var i = 0; i < 5; i++) await store.SetAsync($"k{i}", i);
        Assert.Empty(Directory.GetFiles(store.RootPath, "state.json.tmp-*"));
        Assert.Equal(5, (await store.ListKeysAsync()).Count);
    }

    [Fact]
    public async Task InvalidStateKeyThrows()
    {
        var store = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("has space!", "v"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(new string('a', 129), "v"));
    }

    [Fact]
    public async Task InvalidMigrationVersionsThrow()
    {
        var store = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterMigrationAsync(2, 1, (d, _) => Task.FromResult(d)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterMigrationAsync(1, 1, (d, _) => Task.FromResult(d)));
    }
}
