using PiSharp.Eval.Kernels;
using Xunit;

namespace PiSharp.Eval.Tests;

public sealed class EvalKernelRegistryTests
{
    private sealed class FakeFactory(string name) : IKernelFactory
    {
        public string KernelName { get; } = name;
        public IKernel Create() => new FakeKernel(name);
    }

    private sealed class FakeKernel(string name) : IKernel
    {
        public string Name { get; } = name;
        public string Language => name;
        public bool IsRunning => true;
        public Task StartAsync(KernelStartOptions options, CancellationToken ct = default) => Task.CompletedTask;
        public Task<KernelExecuteResult> ExecuteAsync(string code, KernelExecuteOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new KernelExecuteResult(code, false, 0, false, false));
        public Task<KernelSnapshot> SnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(new KernelSnapshot(1, Name, "1.0", DateTimeOffset.UtcNow, false, [], []));
        public Task RestoreAsync(KernelSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public EvalKernelRegistryTests()
    {
        EvalKernelRegistry.Clear();
    }

    [Fact]
    public void RegisterFactory_RoundTrips()
    {
        EvalKernelRegistry.RegisterFactory(new FakeFactory("fake"));

        var found = EvalKernelRegistry.FindFactory("fake");
        Assert.NotNull(found);
        Assert.Equal("fake", found.KernelName);
        Assert.Contains(EvalKernelRegistry.Factories, f => f.KernelName == "fake");
    }

    [Fact]
    public void RegisterFactory_DuplicateName_Throws()
    {
        EvalKernelRegistry.RegisterFactory(new FakeFactory("dup"));

        Assert.Throws<InvalidOperationException>(() => EvalKernelRegistry.RegisterFactory(new FakeFactory("dup")));
    }

    [Fact]
    public void RegisterFactory_BlankName_Throws()
    {
        Assert.Throws<ArgumentException>(() => EvalKernelRegistry.RegisterFactory(new FakeFactory(" ")));
    }

    [Fact]
    public void FindFactory_Unknown_ReturnsNull()
    {
        Assert.Null(EvalKernelRegistry.FindFactory("nope"));
    }

    [Fact]
    public void Clear_EmptiesRegistry()
    {
        EvalKernelRegistry.RegisterFactory(new FakeFactory("a"));
        EvalKernelRegistry.Clear();
        Assert.Empty(EvalKernelRegistry.Factories);
    }
}
