using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Messages;

namespace PiSharp.Agent.Sessions;

public sealed class Session<TMetadata>(ISessionStorage<TMetadata> storage, ILoggerFactory? loggerFactory = null) : ISession<TMetadata> where TMetadata : ISessionMetadata
{
    private readonly ILogger _logger = loggerFactory?.CreateLogger<Session<TMetadata>>() ?? NullLogger<Session<TMetadata>>.Instance;

    public ISessionStorage<TMetadata> Storage { get; } = storage;
    public TMetadata Metadata => Storage.Metadata;
    public string Id => Storage.Metadata.Id;
    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => Storage.GetLeafIdAsync(cancellationToken);
    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => Storage.GetEntryAsync(id, cancellationToken);
    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => Storage.GetEntriesAsync(cancellationToken);
    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => Storage.GetLabelAsync(id, cancellationToken);
    public async Task<IReadOnlyList<SessionTreeEntry>> GetBranchAsync(string? fromId = null, CancellationToken cancellationToken = default) => await Storage.GetPathToRootAsync(fromId ?? await Storage.GetLeafIdAsync(cancellationToken), cancellationToken);
    public async Task<SessionContext> BuildContextAsync(CancellationToken cancellationToken = default) => BuildSessionContext(await GetBranchAsync(cancellationToken: cancellationToken));
    public async Task<string?> GetSessionNameAsync(CancellationToken cancellationToken = default) => (await Storage.FindEntriesAsync(e => e is SessionInfoEntry, cancellationToken)).OfType<SessionInfoEntry>().LastOrDefault()?.Name?.Trim();
    public Task<string> AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default) => AppendAsync(new MessageEntry { Message = message, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public Task<string> AppendThinkingLevelChangeAsync(string thinkingLevel, CancellationToken cancellationToken = default) => AppendAsync(new ThinkingLevelChangeEntry { ThinkingLevel = thinkingLevel, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public Task<string> AppendModelChangeAsync(string provider, string modelId, CancellationToken cancellationToken = default) => AppendAsync(new ModelChangeEntry { Provider = provider, ModelId = modelId, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public Task<string> AppendCompactionAsync(string summary, string firstKeptEntryId, int tokensBefore, object? details = null, bool? fromHook = null, CancellationToken cancellationToken = default) => AppendAsync(new CompactionEntry { Summary = summary, FirstKeptEntryId = firstKeptEntryId, TokensBefore = tokensBefore, Details = details, FromHook = fromHook, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public Task<string> AppendCustomEntryAsync(string customType, object? data = null, CancellationToken cancellationToken = default) => AppendAsync(new CustomEntry { CustomType = customType, Data = data, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public Task<string> AppendCustomMessageEntryAsync(string customType, object content, bool display, object? details = null, CancellationToken cancellationToken = default) => AppendAsync(new CustomMessageEntry { CustomType = customType, Content = content, Display = display, Details = details, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public async Task<string> AppendLabelAsync(string targetId, string? label, CancellationToken cancellationToken = default) { if (await Storage.GetEntryAsync(targetId, cancellationToken) is null) throw new InvalidOperationException($"Entry {targetId} not found"); return await AppendAsync(new LabelEntry { TargetId = targetId, Label = label, Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken); }
    public Task<string> AppendSessionNameAsync(string name, CancellationToken cancellationToken = default) => AppendAsync(new SessionInfoEntry { Name = name.Trim(), Id = string.Empty, ParentId = null, Timestamp = default }, cancellationToken);
    public async Task<string?> MoveToAsync(string? entryId, BranchSummaryEntry? summary = null, CancellationToken cancellationToken = default) { if (entryId is not null && await Storage.GetEntryAsync(entryId, cancellationToken) is null) throw new InvalidOperationException($"Entry {entryId} not found"); await Storage.SetLeafIdAsync(entryId, cancellationToken); return summary is null ? null : await AppendAsync(summary with { ParentId = entryId }, cancellationToken); }
    public async Task<IReadOnlyList<string>> AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return [];

        var branch = (await GetBranchAsync(cancellationToken: cancellationToken)).ToList();
        var preparedEntries = new List<SessionTreeEntry>(entries.Count);
        var usedIds = branch.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var parentId = await Storage.GetLeafIdAsync(cancellationToken);
        foreach (var entry in entries)
        {
            ValidateAppend(entry, branch);
            string id;
            do
            {
                id = await Storage.CreateEntryIdAsync(cancellationToken);
            } while (!usedIds.Add(id));

            var prepared = entry with { Id = id, ParentId = parentId, Timestamp = DateTimeOffset.UtcNow };
            LogThinkingLevelAppendPrepared(prepared);
            preparedEntries.Add(prepared);
            branch.Add(prepared);
            parentId = prepared.Id;
        }

        await Storage.AppendEntriesAsync(preparedEntries, cancellationToken);

        foreach (var prepared in preparedEntries) LogThinkingLevelAppendStored(prepared);

        return preparedEntries.Select(entry => entry.Id).ToArray();
    }

    private async Task<string> AppendAsync(SessionTreeEntry entry, CancellationToken cancellationToken)
        => (await AppendEntriesAsync([entry], cancellationToken))[0];

    private static void ValidateAppend(SessionTreeEntry entry, IReadOnlyList<SessionTreeEntry> branch)
    {
        if (entry is not MessageEntry { Message: ToolResultMessage result }) return;

        var hasMatchingToolCall = branch.OfType<MessageEntry>()
            .Select(messageEntry => messageEntry.Message)
            .OfType<AssistantMessage>()
            .SelectMany(message => message.Content.OfType<ToolCallContent>())
            .Any(toolCall => StringComparer.Ordinal.Equals(toolCall.Id, result.ToolUseId));
        if (!hasMatchingToolCall)
            throw new InvalidOperationException($"Cannot append ToolResultMessage for tool call '{result.ToolUseId}' because the current session branch has no matching assistant tool call.");
    }

    private void LogThinkingLevelAppendPrepared(SessionTreeEntry entry)
    {
        if (entry is not ThinkingLevelChangeEntry thinkingChange) return;
        _logger.LogDebug(
            "Session append prepared thinking level entry sessionId={SessionId} entryId={EntryId} parentId={ParentId} thinkingLevel={ThinkingLevel}",
            Id,
            entry.Id,
            entry.ParentId,
            thinkingChange.ThinkingLevel);
    }

    private void LogThinkingLevelAppendStored(SessionTreeEntry entry)
    {
        if (entry is not ThinkingLevelChangeEntry thinkingChange) return;
        _logger.LogDebug(
            "Session append stored thinking level entry sessionId={SessionId} entryId={EntryId} parentId={ParentId} thinkingLevel={ThinkingLevel}",
            Id,
            entry.Id,
            entry.ParentId,
            thinkingChange.ThinkingLevel);
    }

    public static SessionContext BuildSessionContext(IReadOnlyList<SessionTreeEntry> pathEntries)
    {
        var thinking = ThinkingLevel.Off; string? provider = null; string? modelId = null; var compaction = pathEntries.OfType<CompactionEntry>().LastOrDefault();
        foreach (var entry in pathEntries) { if (entry is ThinkingLevelChangeEntry t && Enum.TryParse<ThinkingLevel>(t.ThinkingLevel, true, out var parsed)) thinking = parsed; if (entry is ModelChangeEntry m) { provider = m.Provider; modelId = m.ModelId; } if (entry is MessageEntry { Message: AssistantMessage a }) { provider = a.Provider; modelId = a.Model; } }
        var messages = new List<AgentMessage>();
        void Append(SessionTreeEntry entry) { switch (entry) { case MessageEntry m: messages.Add(m.Message); break; case CustomMessageEntry c: messages.Add(CustomMessageContent.ToCustomMessage(c.CustomType, c.Content, c.Display, c.Details)); break; case BranchSummaryEntry b: messages.Add(new BranchSummaryMessage(b.Summary, b.FromId, b.Timestamp)); break; } }
        if (compaction is not null) { messages.Add(new CompactionSummaryMessage(compaction.Summary, compaction.TokensBefore, compaction.Timestamp)); var keep = false; foreach (var entry in pathEntries) { if (entry.Id == compaction.FirstKeptEntryId) keep = true; if (keep && entry.Id != compaction.Id) Append(entry); } } else foreach (var entry in pathEntries) Append(entry);
        return new SessionContext(messages, thinking, provider, modelId);
    }
}
