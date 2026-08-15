using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Coordination;

public sealed class CoordinationJsonlStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private readonly ILogger _logger;

    public CoordinationJsonlStore(string directory, ILogger<CoordinationJsonlStore>? logger = null)
    {
        _directory = directory;
        _logger = logger ?? NullLogger<CoordinationJsonlStore>.Instance;
    }

    private string FilePath => Path.GetFullPath(Path.Combine(_directory, "events.jsonl"));

    public async Task AppendAsync(CoordinationRecord record)
    {
        EnsureDirectory();
        ValidateRecord(record);

        var json = JsonSerializer.Serialize(record, record.GetType(), SerializerOptions);
        var writeLock = WriteLocks.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(FilePath, json + Environment.NewLine);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<CoordinationRecord>> ReadAllAsync()
    {
        if (!File.Exists(FilePath))
            return Array.Empty<CoordinationRecord>();

        var lines = await File.ReadAllLinesAsync(FilePath);
        var records = new List<CoordinationRecord>(lines.Length);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            CoordinationRecord record;
            try
            {
                record = DeserializeRecord(line);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Skip-and-log: a single corrupt or unknown line (e.g. written by a newer version)
                // must not brick the store for every other record in the file. The line stays in
                // place; a future maintenance pass could rewrite the file without skipped lines
                // (rewrite-if-repaired), but that is deliberately not done here.
                _logger.LogWarning(
                    "Skipping corrupt coordination record at line {LineIndex} of '{FilePath}': {Reason}",
                    lineIndex + 1, FilePath, ex.Message);
                continue;
            }

            // Strict validation stays for known types: a well-typed record missing required fields
            // indicates a writer bug and still surfaces as an error rather than being silently dropped.
            ValidateRecord(record);
            records.Add(record);
        }

        return records;
    }

    private static CoordinationRecord DeserializeRecord(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeElement))
            throw new InvalidOperationException("JSONL record missing 'type' discriminator.");

        var type = typeElement.GetString();

        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("JSONL record has a blank 'type' discriminator.");

        CoordinationRecord? record = type switch
        {
            "agent_registered" => JsonSerializer.Deserialize<AgentRegisteredRecord>(json, SerializerOptions),
            "agent_unregistered" => JsonSerializer.Deserialize<AgentUnregisteredRecord>(json, SerializerOptions),
            "agent_heartbeat" => JsonSerializer.Deserialize<AgentHeartbeatRecord>(json, SerializerOptions),
            "message_sent" => JsonSerializer.Deserialize<MessageSentRecord>(json, SerializerOptions),
            "file_read" => JsonSerializer.Deserialize<FileReadRecord>(json, SerializerOptions),
            "file_write" => JsonSerializer.Deserialize<FileWriteRecord>(json, SerializerOptions),
            "preflight_warning" => JsonSerializer.Deserialize<PreflightWarningRecord>(json, SerializerOptions),
            "subagent_observed" => JsonSerializer.Deserialize<SubagentObservedRecord>(json, SerializerOptions),
            _ => throw new InvalidOperationException($"Unknown coordination record type: '{type}'."),
        };

        if (record is null)
            throw new InvalidOperationException($"Failed to deserialize coordination record of type '{type}'.");

        return record;
    }

    /// <summary>
    /// Releases the process-wide write lock entry for this store's file path. In-flight appends
    /// already hold their own <see cref="SemaphoreSlim"/> reference, so removing the entry cannot
    /// break them; it only stops short-lived stores from accumulating entries in the static map.
    /// </summary>
    public void Dispose() => WriteLocks.TryRemove(FilePath, out _);

    private static void ValidateRecord(CoordinationRecord record)
    {
        if (record.Timestamp == default)
            throw new InvalidOperationException("Coordination record is missing a valid Timestamp.");

        switch (record)
        {
            case AgentRegisteredRecord r:
                AssertNonBlank(r.AgentId, nameof(r.AgentId));
                AssertNonBlank(r.Cwd, nameof(r.Cwd));
                break;
            case AgentUnregisteredRecord r:
                AssertNonBlank(r.AgentId, nameof(r.AgentId));
                break;
            case AgentHeartbeatRecord r:
                AssertNonBlank(r.AgentId, nameof(r.AgentId));
                break;
            case MessageSentRecord r:
                AssertNonBlank(r.MessageId, nameof(r.MessageId));
                AssertNonBlank(r.FromAgentId, nameof(r.FromAgentId));
                AssertNonBlank(r.To, nameof(r.To));
                AssertNonBlank(r.Body, nameof(r.Body));
                break;
            case FileReadRecord r:
                AssertNonBlank(r.AgentId, nameof(r.AgentId));
                AssertNonBlank(r.Path, nameof(r.Path));
                break;
            case FileWriteRecord r:
                AssertNonBlank(r.AgentId, nameof(r.AgentId));
                AssertNonBlank(r.Path, nameof(r.Path));
                break;
            case PreflightWarningRecord r:
                AssertNonBlank(r.AgentId, nameof(r.AgentId));
                AssertNonBlank(r.Path, nameof(r.Path));
                AssertNonBlank(r.ConflictingAgentId, nameof(r.ConflictingAgentId));
                if (r.ConflictingTimestamp == default)
                    throw new InvalidOperationException(
                        $"Required field 'ConflictingTimestamp' is default in coordination record.");
                if (r.WarningTimestamp == default)
                    throw new InvalidOperationException(
                        $"Required field 'WarningTimestamp' is default in coordination record.");
                break;
            case SubagentObservedRecord r:
                AssertNonBlank(r.SubagentId, nameof(r.SubagentId));
                AssertNonBlank(r.EventName, nameof(r.EventName));
                if (!PiSubagentsEventAdapter.IsKnownEventName(r.EventName))
                    throw new InvalidOperationException(
                        $"Unknown subagent event name '{r.EventName}' in coordination record.");
                if (string.IsNullOrWhiteSpace(r.Cwd))
                    throw new InvalidOperationException(
                        $"Required field 'Cwd' is null or blank in subagent observed record.");
                break;
        }
    }

    private static void AssertNonBlank(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Required field '{fieldName}' is null or blank in coordination record.");
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
    }
}
