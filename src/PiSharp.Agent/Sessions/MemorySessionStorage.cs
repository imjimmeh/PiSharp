using PiSharp.Abstractions.Sessions;

namespace PiSharp.Agent.Sessions;

public sealed class MemorySessionStorage<TMetadata> : ISessionStorage<TMetadata> where TMetadata : ISessionMetadata
{
    private readonly TMetadata _metadata;
    private readonly List<SessionTreeEntry> _entries;
    private readonly Dictionary<string, SessionTreeEntry> _byId;
    private readonly Dictionary<string, string> _labelsById = new();
    private readonly bool _writeLeafEntries;
    private string? _leafId;

    public MemorySessionStorage(TMetadata metadata, IEnumerable<SessionTreeEntry>? entries = null, bool writeLeafEntries = false, string? initialLeafId = null)
    {
        _metadata = metadata;
        _writeLeafEntries = writeLeafEntries;
        _entries = entries?.ToList() ?? [];
        _byId = _entries.ToDictionary(e => e.Id);
        foreach (var entry in _entries)
        {
            UpdateLabelCache(entry);
            _leafId = LeafIdAfter(entry);
        }
        if (initialLeafId is not null) _leafId = initialLeafId;
    }

    public TMetadata Metadata => _metadata;
    public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) => Task.FromResult(_metadata);
    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(_leafId is null || _byId.ContainsKey(_leafId) ? _leafId : throw new InvalidOperationException($"Entry {_leafId} not found"));
    public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(SessionRepoUtils.CreateEntryId(_byId.ContainsKey));
    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_byId.GetValueOrDefault(id));
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(Func<SessionTreeEntry, bool> predicate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionTreeEntry>>(_entries.Where(predicate).ToArray());
    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_labelsById.GetValueOrDefault(id));
    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionTreeEntry>>(_entries.ToArray());
    public async Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default)
    {
        if (leafId is not null && !_byId.ContainsKey(leafId)) throw new InvalidOperationException($"Entry {leafId} not found");
        if (!_writeLeafEntries) { _leafId = leafId; return; }
        await AppendEntryAsync(new LeafEntry { Id = await CreateEntryIdAsync(cancellationToken), ParentId = _leafId, Timestamp = DateTimeOffset.UtcNow, TargetId = leafId }, cancellationToken);
    }
    public Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default) => AppendEntriesAsync([entry], cancellationToken);
    public Task AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default) { foreach (var entry in entries) { _entries.Add(entry); _byId[entry.Id] = entry; UpdateLabelCache(entry); _leafId = LeafIdAfter(entry); } return Task.CompletedTask; }
    public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default) { if (leafId is null) return Task.FromResult<IReadOnlyList<SessionTreeEntry>>([]); var path = new List<SessionTreeEntry>(); var current = _byId.GetValueOrDefault(leafId) ?? throw new InvalidOperationException($"Entry {leafId} not found"); while (true) { path.Insert(0, current); if (current.ParentId is null) break; current = _byId.GetValueOrDefault(current.ParentId) ?? throw new InvalidOperationException($"Entry {current.ParentId} not found"); } return Task.FromResult<IReadOnlyList<SessionTreeEntry>>(path); }

    private void UpdateLabelCache(SessionTreeEntry entry) { if (entry is not LabelEntry label) return; var trimmed = label.Label?.Trim(); if (string.IsNullOrEmpty(trimmed)) _labelsById.Remove(label.TargetId); else _labelsById[label.TargetId] = trimmed; }
    private static string? LeafIdAfter(SessionTreeEntry entry) => entry is LeafEntry leaf ? leaf.TargetId : entry.Id;
}
