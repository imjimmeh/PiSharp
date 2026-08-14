using PiSharp.Memory.Abstractions;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class MemoryServicesRegistryTests
{
    private sealed class StubProvider(string id) : IMemoryProvider
    {
        public string Id { get; } = id;
        public string DisplayName => $"Stub {Id}";
        public bool SupportsSemanticSearch => false;

        public Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default) => Task.FromResult<MemoryRecord?>(null);
        public Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task<MemoryRecord?> UpdateAsync(MemoryScope scope, string recordKey, Func<MemoryRecord, MemoryRecord> mutate, CancellationToken ct = default) => Task.FromResult<MemoryRecord?>(null);
        public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryRecord>>([]);
        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);
        public Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryRecord>>([]);
    }

    [Fact]
    public void Register_TryGet_ReturnsRegisteredProvider()
    {
        var registry = new MemoryProviderRegistry();
        var provider = new StubProvider("file");
        registry.Register(provider);

        Assert.Same(provider, registry.TryGet("file"));
    }

    [Fact]
    public void Register_LastRegistrationWinsPerId()
    {
        var registry = new MemoryProviderRegistry();
        registry.Register(new StubProvider("file"));
        var replacement = new StubProvider("file");
        registry.Register(replacement);

        Assert.Same(replacement, registry.TryGet("file"));
    }

    [Fact]
    public void TryGet_UnknownOrEmptyId_ReturnsNull()
    {
        var registry = new MemoryProviderRegistry();
        registry.Register(new StubProvider("file"));

        Assert.Null(registry.TryGet("vector"));
        Assert.Null(registry.TryGet(string.Empty));
        Assert.Null(registry.TryGet("  "));
        Assert.Null(registry.TryGet(null!));
    }

    [Fact]
    public void Register_NullProvider_Throws()
    {
        var registry = new MemoryProviderRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void Register_EmptyId_Throws()
    {
        var registry = new MemoryProviderRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new StubProvider(" ")));
    }

    [Fact]
    public void All_ReturnsSnapshotOfRegisteredProviders()
    {
        var registry = new MemoryProviderRegistry();
        registry.Register(new StubProvider("off"));
        registry.Register(new StubProvider("file"));

        Assert.Equal(["off", "file"], registry.All.Select(provider => provider.Id).ToArray());
    }
}
