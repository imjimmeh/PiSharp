using PiSharp.Abstractions.Messages;

namespace PiSharp.Abstractions.Sessions;

public interface ISession<TMetadata>
    where TMetadata : ISessionMetadata
{
    TMetadata Metadata { get; }
    string Id { get; }
    string? LeafId { get; set; }
    ISessionStorage<TMetadata> Storage { get; }
    Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default);
    Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionTreeEntry>> GetBranchAsync(string? fromId = null, CancellationToken cancellationToken = default);
    Task<SessionContext> BuildContextAsync(CancellationToken cancellationToken = default);
    Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default);
    Task<string?> GetSessionNameAsync(CancellationToken cancellationToken = default);
    Task<string> AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default);
    Task<string> AppendThinkingLevelChangeAsync(string thinkingLevel, CancellationToken cancellationToken = default);
    Task<string> AppendModelChangeAsync(string provider, string modelId, CancellationToken cancellationToken = default);
    Task<string> AppendCompactionAsync(string summary, string firstKeptEntryId, int tokensBefore, object? details = null, bool? fromHook = null, CancellationToken cancellationToken = default);
    Task<string> AppendCustomEntryAsync(string customType, object? data = null, CancellationToken cancellationToken = default);
    Task<string> AppendCustomMessageEntryAsync(string customType, object content, bool display, object? details = null, CancellationToken cancellationToken = default);
    Task<string> AppendLabelAsync(string targetId, string? label, CancellationToken cancellationToken = default);
    Task<string> AppendSessionNameAsync(string name, CancellationToken cancellationToken = default);
    Task<string?> MoveToAsync(string? entryId, BranchSummaryEntry? summary = null, CancellationToken cancellationToken = default);
}
