using PiSharp.Memory.Abstractions;

namespace PiSharp.Memory;

/// <summary>
/// Default <see cref="IMemoryStore"/>: a thin facade over the active provider.
/// Project scope binds the provider's project key (the provider resolves it from
/// its construction cwd); user scope is global. Also exposes <see cref="ProjectKey"/>
/// for diagnostics.
/// </summary>
public sealed class MemoryStore(IMemoryProvider provider, string projectKey) : IMemoryStore
{
    public string ProjectKey { get; } = projectKey;

    public IMemoryProvider Provider { get; } = provider;

    public Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default)
        => Provider.GetAsync(scope, recordKey, ct);

    public Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default)
        => Provider.PutAsync(scope, record, ct);

    public Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default)
        => Provider.DeleteAsync(scope, recordKey, ct);

    public Task<MemoryRecord?> UpdateAsync(
        MemoryScope scope,
        string recordKey,
        Func<MemoryRecord, MemoryRecord> mutate,
        CancellationToken ct = default)
        => Provider.UpdateAsync(scope, recordKey, mutate, ct);

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default)
        => Provider.ListAsync(scope, query, ct);

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default)
        => Provider.SearchAsync(scope, text, limit, ct);

    public Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default)
        => Provider.RecallAsync(scope, query, ct);
}
