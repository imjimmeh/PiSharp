using PiSharp.Memory.Abstractions;

namespace PiSharp.Memory.Backends.Off;

/// <summary>
/// No-op backend ("off"). Every read returns null/empty so the core plugin's
/// tool layer can answer with the "backend is off" blocked result.
/// </summary>
public sealed class OffMemoryProvider : IMemoryProvider
{
    public string Id => "off";
    public string DisplayName => "Off (memory disabled)";
    public bool SupportsSemanticSearch => false;

    public Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default)
        => Task.FromResult<MemoryRecord?>(null);

    public Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<MemoryRecord?> UpdateAsync(
        MemoryScope scope,
        string recordKey,
        Func<MemoryRecord, MemoryRecord> mutate,
        CancellationToken ct = default)
        => Task.FromResult<MemoryRecord?>(null);

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryRecord>>([]);

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

    public Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryRecord>>([]);
}
