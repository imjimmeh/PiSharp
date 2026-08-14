using PiSharp.Eval.Kernel.CSharp;
using PiSharp.Eval.Kernels;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// Per-runtime kernel registry tests: factory lookup, lazy create/start, serialized
/// per-kernel gate, restore-on-start, and dispose-all teardown.
/// </summary>
public sealed class KernelRegistryTests
{
    public KernelRegistryTests()
    {
        EvalKernelRegistry.Clear();
        EvalKernelRegistry.RegisterFactory(new CSharpKernelFactory());
    }

    [Fact]
    public async Task GetOrStartAsync_CreatesKernelFromRegisteredFactory()
    {
        await using var registry = new KernelRegistry(Path.GetTempPath());

        var kernel = await registry.GetOrStartAsync("csharp");

        Assert.NotNull(kernel);
        Assert.Equal("csharp", kernel.Name);
        Assert.True(kernel.IsRunning);
        Assert.True(registry.Has("csharp"));
        Assert.Same(kernel, registry.Get("csharp"));
    }

    [Fact]
    public async Task GetOrStartAsync_UnknownKernel_Throws()
    {
        await using var registry = new KernelRegistry(Path.GetTempPath());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetOrStartAsync("python"));
        Assert.Contains("python", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_StatePersistsAcrossCalls()
    {
        await using var registry = new KernelRegistry(Path.GetTempPath());

        await registry.ExecuteAsync("csharp", "int answer = 42;");
        var result = await registry.ExecuteAsync("csharp", "answer + 8");

        Assert.False(result.IsError);
        Assert.Contains("50", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentCallsSerializeOnKernelGate()
    {
        await using var registry = new KernelRegistry(Path.GetTempPath());
        await registry.ExecuteAsync("csharp", "int counter = 0;");

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => registry.ExecuteAsync("csharp", "counter++;"))
            .ToArray();
        await Task.WhenAll(tasks);

        var result = await registry.ExecuteAsync("csharp", "counter");
        Assert.False(result.IsError);
        Assert.Contains("4", result.Output);
    }

    [Fact]
    public async Task RestoreOnStart_RestoresPersistedSnapshot()
    {
        KernelSnapshot? persisted = null;
        var store = new KernelSnapshotStore(state: null,
            fallbackRoot: Path.Combine(Path.GetTempPath(), $"eval-restore-{Guid.NewGuid():N}"));

        await using (var first = new KernelRegistry(Path.GetTempPath()))
        {
            await first.ExecuteAsync("csharp", "int answer = 42; string tag = \"restored\";");
            persisted = await first.SnapshotAsync("csharp");
            await store.SaveAsync("sess-1", "csharp", persisted);
        }

        // A fresh registry for the same session restores on start (same session id).
        await using var second = new KernelRegistry(Path.GetTempPath(), (IKernelToolBridge?)null, new KernelRegistryOptions
        {
            SessionId = "sess-1",
            RestoreOnStart = true,
            SnapshotProvider = (kernelName, sessionId, ct) => store.LoadAsync(sessionId, kernelName, ct),
        });

        var result = await second.ExecuteAsync("csharp", "answer + tag.Length");
        Assert.False(result.IsError);
        Assert.Contains("50", result.Output);
    }

    [Fact]
    public async Task DisposeAllAsync_DisposesKernels()
    {
        await using var registry = new KernelRegistry(Path.GetTempPath());
        await registry.GetOrStartAsync("csharp");

        var disposed = false;
        await registry.DisposeAllAsync((kernel, _) =>
        {
            disposed = kernel is CSharpKernel { IsRunning: true };
            return Task.CompletedTask;
        });

        Assert.True(disposed);
        Assert.Empty(registry.Kernels);
    }

    [Fact]
    public async Task DisposeAllAsync_BeforeDisposeFailure_StillDisposes()
    {
        await using var registry = new KernelRegistry(Path.GetTempPath());
        await registry.GetOrStartAsync("csharp");

        await registry.DisposeAllAsync((_, _) => throw new InvalidOperationException("snapshot failed"));
        Assert.Empty(registry.Kernels);
    }
}
