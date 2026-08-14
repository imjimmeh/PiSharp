using PiSharp.Eval;
using PiSharp.Eval.Kernels;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// Snapshot persistence tests: file-fallback round-trip, key format, and the size cap
/// that degrades over-cap snapshots to lossy (names/types kept, values dropped).
/// </summary>
public sealed class KernelSnapshotStoreTests
{
    private static KernelSnapshot SampleSnapshot(bool lossy = false) => new(
        SchemaVersion: 1,
        KernelName: "csharp",
        KernelVersion: "1.0.0",
        CreatedAt: DateTimeOffset.UtcNow,
        Lossy: lossy,
        Variables:
        [
            new KernelVariableSnapshot("answer", "System.Int32", "42", false),
            new KernelVariableSnapshot("callback", "System.Action", null, true),
        ],
        Imports: ["System.Linq"]);

    [Fact]
    public void Key_UsesSessionAndKernel()
    {
        Assert.Equal("kernels.sess-1.csharp", KernelSnapshotStore.Key("sess-1", "csharp"));
    }

    [Fact]
    public async Task SaveLoad_RoundTripsThroughFileFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eval-store-{Guid.NewGuid():N}");
        try
        {
            var store = new KernelSnapshotStore(state: null, fallbackRoot: root);
            var snapshot = SampleSnapshot();

            await store.SaveAsync("sess-1", "csharp", snapshot);
            var loaded = await store.LoadAsync("sess-1", "csharp");

            Assert.NotNull(loaded);
            Assert.Equal("csharp", loaded.KernelName);
            Assert.Equal(2, loaded.Variables.Count);
            Assert.Equal("42", loaded.Variables[0].Json);
            Assert.True(loaded.Variables[1].Lossy);
            Assert.Equal(["System.Linq"], loaded.Imports);
            Assert.Equal(snapshot, store.LastSaved("sess-1", "csharp"));
            Assert.True(store.TryGetBytes("sess-1", "csharp", out var bytes) && bytes > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_Missing_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eval-store-{Guid.NewGuid():N}");
        try
        {
            var store = new KernelSnapshotStore(state: null, fallbackRoot: root);
            Assert.Null(await store.LoadAsync("sess-1", "csharp"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OverCapSnapshot_StoredLossy_ValuesDropped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eval-store-{Guid.NewGuid():N}");
        try
        {
            var store = new KernelSnapshotStore(state: null, fallbackRoot: root, maxBytes: 128);
            var snapshot = SampleSnapshot();

            await store.SaveAsync("sess-1", "csharp", snapshot);
            var loaded = await store.LoadAsync("sess-1", "csharp");

            Assert.NotNull(loaded);
            Assert.True(loaded.Lossy);
            Assert.All(loaded.Variables, v => Assert.True(v.Lossy));
            Assert.All(loaded.Variables, v => Assert.Null(v.Json));
            // Names/types are preserved even when values are dropped.
            Assert.Equal("answer", loaded.Variables[0].Name);
            Assert.Equal("System.Int32", loaded.Variables[0].TypeName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
