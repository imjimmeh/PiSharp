namespace PiSharp.Abstractions.Sessions;

public interface ISessionStorage<TMetadata>
    where TMetadata : ISessionMetadata
{
    TMetadata Metadata { get; }

    Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) => Task.FromResult(Metadata);

    Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default);

    Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default);

    Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default);

    Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default);

    async Task AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
        {
            await AppendEntryAsync(entry, cancellationToken);
        }
    }

    Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(Func<SessionTreeEntry, bool> predicate, CancellationToken cancellationToken = default);

    Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
}
