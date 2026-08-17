using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Serialization;

namespace PiSharp.Agent.Sessions;

public sealed class JsonlSessionStorage : ISessionStorage<JsonlSessionMetadata>
{
    private readonly IFileSystem _fs;
    private readonly string _filePath;
    private readonly MemorySessionStorage<JsonlSessionMetadata> _inner;
    private readonly bool _writeLeafEntries;
    private readonly ILogger _logger;
    private readonly SessionHeader _header;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _headerWritten;
    private bool _hasUserMessage;

    private JsonlSessionStorage(IFileSystem fs, string filePath, SessionHeader header, JsonlSessionMetadata metadata, IReadOnlyList<SessionTreeEntry> entries, bool writeLeafEntries, bool headerWritten, string? initialLeafId = null, ILoggerFactory? loggerFactory = null)
    {
        _fs = fs;
        _filePath = filePath;
        _writeLeafEntries = writeLeafEntries;
        _header = header;
        _headerWritten = headerWritten;
        _inner = new MemorySessionStorage<JsonlSessionMetadata>(metadata, entries, writeLeafEntries, initialLeafId);
        _logger = loggerFactory?.CreateLogger<JsonlSessionStorage>() ?? NullLogger<JsonlSessionStorage>.Instance;
    }

    public static Task<JsonlSessionStorage> CreateAsync(IFileSystem fs, string filePath, string cwd, string sessionId, string? parentSessionPath, CancellationToken cancellationToken)
        => CreateAsync(fs, filePath, cwd, sessionId, parentSessionPath, false, cancellationToken: cancellationToken);

    public static async Task<JsonlSessionStorage> CreateAsync(IFileSystem fs, string filePath, string cwd, string sessionId, string? parentSessionPath = null, bool writeLeafEntries = false, bool persistImmediately = false, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null)
    {
        var header = new SessionHeader("session", 3, sessionId, DateTimeOffset.UtcNow, cwd, parentSessionPath);
        if (persistImmediately) await fs.WriteFileAsync(filePath, JsonSerializer.Serialize(header, AgentJsonSerializer.Options) + "\n", cancellationToken).GetOrThrowAsync();
        return new JsonlSessionStorage(fs, filePath, header, new JsonlSessionMetadata(sessionId, header.Timestamp, cwd, filePath, parentSessionPath), [], writeLeafEntries, headerWritten: persistImmediately, loggerFactory: loggerFactory);
    }

    public static Task<JsonlSessionStorage> OpenAsync(IFileSystem fs, string filePath, CancellationToken cancellationToken)
        => OpenAsync(fs, filePath, false, cancellationToken);

    public static async Task<JsonlSessionStorage> OpenAsync(IFileSystem fs, string filePath, bool writeLeafEntries = false, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null)
    {
        var content = await fs.ReadTextFileAsync(filePath, cancellationToken).GetOrThrowAsync();
        var lines = content.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length == 0) throw new InvalidOperationException("Invalid JSONL session file: missing session header");
        var header = JsonSerializer.Deserialize<SessionHeader>(lines[0], AgentJsonSerializer.Options) ?? throw new InvalidOperationException("Invalid JSONL session header");
        if (header.Type != "session" || header.Version is <= 0 or > 3) throw new InvalidOperationException("Unsupported JSONL session header");
        var allEntries = lines.Skip(1).Select(line => ReadEntryOrNull(line)).OfType<SessionTreeEntry>().ToArray();
        var legacyLeafTargetId = allEntries.OfType<LeafEntry>().LastOrDefault()?.TargetId;
        var entries = writeLeafEntries ? allEntries : allEntries.Where(entry => entry is not LeafEntry).ToArray();
        return new JsonlSessionStorage(fs, filePath, header, new JsonlSessionMetadata(header.Id, header.Timestamp, header.Cwd, filePath, header.ParentSession), entries, writeLeafEntries, headerWritten: true, writeLeafEntries ? null : legacyLeafTargetId, loggerFactory: loggerFactory);
    }

    public static async Task<JsonlSessionMetadata> LoadMetadataAsync(IFileSystem fs, string filePath, CancellationToken cancellationToken = default)
    {
        var content = await fs.ReadTextFileAsync(filePath, cancellationToken).GetOrThrowAsync();
        var lines = content.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length == 0) throw new InvalidOperationException("Invalid JSONL session file: missing session header");
        var header = JsonSerializer.Deserialize<SessionHeader>(lines[0], AgentJsonSerializer.Options) ?? throw new InvalidOperationException("Invalid JSONL session header");
        if (header.Type != "session" || header.Version is <= 0 or > 3) throw new InvalidOperationException("Unsupported JSONL session header");

        var name = (string?)null;
        var messageCount = 0;
        var messageTexts = new List<string>();
        var firstMessage = (string?)null;
        var modifiedAt = (DateTimeOffset?)null;

        foreach (var entry in lines.Skip(1).Select(line => ReadEntryOrNull(line)).OfType<SessionTreeEntry>())
        {
            if (entry is SessionInfoEntry info) name = info.Name;
            if (entry is not MessageEntry { Message: UserMessage or AssistantMessage } messageEntry) continue;

            messageCount++;
            modifiedAt = messageEntry.Message.Timestamp;
            var text = TextFromMessage(messageEntry.Message);
            if (string.IsNullOrWhiteSpace(text)) continue;
            messageTexts.Add(text);
            if (firstMessage is null && messageEntry.Message is UserMessage) firstMessage = text;
        }

        var fallbackModifiedAt = header.Timestamp == default
            ? (await fs.GetFileInfoAsync(filePath, cancellationToken).GetOrThrowAsync()).ModifiedAt
            : header.Timestamp;
        return new JsonlSessionMetadata(
            header.Id,
            header.Timestamp,
            header.Cwd,
            filePath,
            header.ParentSession,
            modifiedAt ?? fallbackModifiedAt,
            messageCount,
            firstMessage ?? "(no messages)",
            string.Join("\n", messageTexts),
            name);
    }

    public JsonlSessionMetadata Metadata => _inner.Metadata;
    public Task<JsonlSessionMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(cancellationToken);
    public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => _inner.GetLeafIdAsync(cancellationToken);
    public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) => _inner.CreateEntryIdAsync(cancellationToken);
    public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => _inner.GetEntryAsync(id, cancellationToken);
    public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(Func<SessionTreeEntry, bool> predicate, CancellationToken cancellationToken = default) => _inner.FindEntriesAsync(predicate, cancellationToken);
    public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => _inner.GetLabelAsync(id, cancellationToken);
    public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.GetPathToRootAsync(leafId, cancellationToken);
    public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => _inner.GetEntriesAsync(cancellationToken);

    public async Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default)
    {
        if (leafId is not null && await _inner.GetEntryAsync(leafId, cancellationToken) is null) throw new InvalidOperationException($"Entry {leafId} not found");
        if (!_writeLeafEntries) { await _inner.SetLeafIdAsync(leafId, cancellationToken); return; }
        await AppendEntryAsync(new LeafEntry { Id = await CreateEntryIdAsync(cancellationToken), ParentId = await GetLeafIdAsync(cancellationToken), Timestamp = DateTimeOffset.UtcNow, TargetId = leafId }, cancellationToken);
    }

    public Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
        => AppendEntriesAsync([entry], cancellationToken);

    public async Task AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return;

        foreach (var thinkingChangeBeforeGate in entries.OfType<ThinkingLevelChangeEntry>())
        {
            _logger.LogDebug(
                "JSONL session append waiting for gate file={FilePath} entryId={EntryId} thinkingLevel={ThinkingLevel}",
                _filePath,
                thinkingChangeBeforeGate.Id,
                thinkingChangeBeforeGate.ThinkingLevel);
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var thinkingChangeAfterGate in entries.OfType<ThinkingLevelChangeEntry>())
            {
                _logger.LogDebug(
                    "JSONL session append acquired gate file={FilePath} entryId={EntryId} thinkingLevel={ThinkingLevel}",
                    _filePath,
                    thinkingChangeAfterGate.Id,
                    thinkingChangeAfterGate.ThinkingLevel);
            }

            foreach (var thinkingChange in entries.OfType<ThinkingLevelChangeEntry>())
            {
                _logger.LogDebug(
                    "JSONL session append starting file={FilePath} entryId={EntryId} parentId={ParentId} thinkingLevel={ThinkingLevel}",
                    _filePath,
                    thinkingChange.Id,
                    thinkingChange.ParentId,
                    thinkingChange.ThinkingLevel);
            }

            // Update in-memory state first so we can flush all queued entries on first write.
            await _inner.AppendEntriesAsync(entries, cancellationToken);

            // Detect the first user message: this is the trigger for writing to disk.
            if (!_hasUserMessage && entries.OfType<MessageEntry>().Any(e => e.Message is UserMessage))
                _hasUserMessage = true;

            // Defer file creation until the first user message is received.
            // Sessions that were created with persistImmediately=true already have _headerWritten=true
            // and bypass this guard (used for forks and subagents).
            if (!_hasUserMessage && !_headerWritten) return;

            if (!_headerWritten)
            {
                // First write to disk: flush header + all queued in-memory entries at once.
                await EnsureHeaderWrittenAsync(cancellationToken);

                foreach (var thinkingChangeAfterHeader in entries.OfType<ThinkingLevelChangeEntry>())
                {
                    _logger.LogDebug(
                        "JSONL session header ensured file={FilePath} entryId={EntryId} thinkingLevel={ThinkingLevel}",
                        _filePath,
                        thinkingChangeAfterHeader.Id,
                        thinkingChangeAfterHeader.ThinkingLevel);
                }

                var allEntries = await _inner.GetEntriesAsync(cancellationToken);
                await _fs.AppendFileAsync(_filePath, string.Concat(allEntries.Select(AgentJsonSerializer.ToJsonLine)), cancellationToken).GetOrThrowAsync();
            }
            else
            {
                foreach (var thinkingChangeAfterHeader in entries.OfType<ThinkingLevelChangeEntry>())
                {
                    _logger.LogDebug(
                        "JSONL session header ensured file={FilePath} entryId={EntryId} thinkingLevel={ThinkingLevel}",
                        _filePath,
                        thinkingChangeAfterHeader.Id,
                        thinkingChangeAfterHeader.ThinkingLevel);
                }

                await _fs.AppendFileAsync(_filePath, string.Concat(entries.Select(AgentJsonSerializer.ToJsonLine)), cancellationToken).GetOrThrowAsync();
            }

            foreach (var thinkingChangeAfterFile in entries.OfType<ThinkingLevelChangeEntry>())
            {
                _logger.LogDebug(
                    "JSONL session file append completed file={FilePath} entryId={EntryId} thinkingLevel={ThinkingLevel}",
                    _filePath,
                    thinkingChangeAfterFile.Id,
                    thinkingChangeAfterFile.ThinkingLevel);
            }

            foreach (var thinkingChangeAfterMemory in entries.OfType<ThinkingLevelChangeEntry>())
            {
                _logger.LogDebug(
                    "JSONL session memory append completed file={FilePath} entryId={EntryId} thinkingLevel={ThinkingLevel}",
                    _filePath,
                    thinkingChangeAfterMemory.Id,
                    thinkingChangeAfterMemory.ThinkingLevel);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task EnsureHeaderWrittenAsync(CancellationToken cancellationToken)
    {
        if (_headerWritten) return;

        _logger.LogDebug(
            "JSONL session header write starting file={FilePath}",
            _filePath);

        await _fs.WriteFileAsync(_filePath, JsonSerializer.Serialize(_header, AgentJsonSerializer.Options) + "\n", cancellationToken).GetOrThrowAsync();

        _logger.LogDebug(
            "JSONL session header write completed file={FilePath}",
            _filePath);

        _headerWritten = true;
    }

    private static SessionTreeEntry? ReadEntryOrNull(string line, ILogger? logger = null)
    {
        try { return AgentJsonSerializer.ReadSessionEntry(line); }
        catch (JsonException ex) { (logger ?? NullLogger.Instance).LogDebug(ex, "Session JSON parse skipped"); return null; }
    }

    private static string TextFromMessage(AgentMessage message)
        => string.Join("\n", message switch
        {
            UserMessage user => user.Content.OfType<TextContent>().Select(content => content.Text),
            AssistantMessage assistant => assistant.Content.OfType<TextContent>().Select(content => content.Text),
            _ => []
        });

    private sealed record SessionHeader(string Type, int Version, string Id, DateTimeOffset Timestamp, string Cwd, string? ParentSession);
}
