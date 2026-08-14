using System.Text;
using System.Text.Json;
using PiSharp.ContinualHarness.Contracts;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Serialization for the append-only <c>refinements.jsonl</c> journal. Mirrors the session-JSONL
/// precedent: the first line is a typed + versioned header, then one compact JSON record per line.
/// </summary>
public static class HarnessJournalFormat
{
    public static string WriteHeader(HarnessRefinementScope scope, string cwd)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", HarnessStore.JournalType);
            writer.WriteNumber("version", HarnessStore.JournalSchemaVersion);
            writer.WriteString("scope", scope.ToString().ToLowerInvariant());
            writer.WriteString("createdAt", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteString("cwd", cwd);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Validates the header line, returning a diagnostic message or null when valid.</summary>
    public static string? ParseHeader(string line, out JsonElement root)
    {
        root = default;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "journal does not start with a valid JSON header line";
        }
        finally
        {
            doc?.Dispose();
        }

        if (!root.TryGetProperty("type", out var type) || type.GetString() != HarnessStore.JournalType)
            return $"header 'type' must be '{HarnessStore.JournalType}'";
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != HarnessStore.JournalSchemaVersion)
            return $"header 'version' must be {HarnessStore.JournalSchemaVersion}";
        return null;
    }

    public static string SerializeRecord(HarnessRefinementRecord record)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("refinementId", record.RefinementId);
            writer.WriteString("timestamp", record.Timestamp.ToString("O"));
            writer.WriteString("scope", record.Scope.ToString().ToLowerInvariant());
            writer.WriteString("kind", record.Kind.ToString().ToLowerInvariant());
            writer.WriteString("name", record.Name);
            writer.WriteString("action", record.Action.ToString().ToLowerInvariant());
            writer.WriteNumber("version", record.Version);
            writer.WritePropertyName("content");
            writer.WriteRawValue(record.Content.GetRawText(), skipInputValidation: true);
            writer.WriteString("author", record.Author);

            if (record.Evidence.Count > 0)
            {
                writer.WritePropertyName("evidence");
                writer.WriteStartArray();
                foreach (var evidence in record.Evidence)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", evidence.SessionId);
                    if (evidence.EntryId is not null) writer.WriteString("entryId", evidence.EntryId);
                    if (evidence.Excerpt is not null) writer.WriteString("excerpt", evidence.Excerpt);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (record.SyncedWith is not null)
            {
                writer.WritePropertyName("syncedWith");
                writer.WriteStartObject();
                if (record.SyncedWith.Path is not null) writer.WriteString("path", record.SyncedWith.Path);
                if (record.SyncedWith.FileMtimeUtc is not null) writer.WriteString("fileMtimeUtc", record.SyncedWith.FileMtimeUtc.Value.ToString("O"));
                if (record.SyncedWith.Sha256 is not null) writer.WriteString("sha256", record.SyncedWith.Sha256);
                if (record.SyncedWith.ApiUpdatedAt is not null) writer.WriteString("apiUpdatedAt", record.SyncedWith.ApiUpdatedAt.Value.ToString("O"));
                writer.WriteEndObject();
            }

            if (record.TargetVersion is not null) writer.WriteNumber("targetVersion", record.TargetVersion.Value);
            if (record.Reason is not null) writer.WriteString("reason", record.Reason);
            if (record.Deleted) writer.WriteBoolean("deleted", record.Deleted);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static HarnessRefinementRecord DeserializeRecord(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        var evidence = new List<RefinementEvidence>();
        if (root.TryGetProperty("evidence", out var evidenceEl) && evidenceEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in evidenceEl.EnumerateArray())
            {
                evidence.Add(new RefinementEvidence(
                    item.TryGetProperty("sessionId", out var sid) ? sid.GetString()! : string.Empty,
                    item.TryGetProperty("entryId", out var eid) ? eid.GetString() : null,
                    item.TryGetProperty("excerpt", out var ex) ? ex.GetString() : null));
            }
        }

        HarnessSyncedWith? syncedWith = null;
        if (root.TryGetProperty("syncedWith", out var sw) && sw.ValueKind == JsonValueKind.Object)
        {
            syncedWith = new HarnessSyncedWith(
                sw.TryGetProperty("path", out var p) ? p.GetString() : null,
                sw.TryGetProperty("fileMtimeUtc", out var mt) && DateTimeOffset.TryParse(mt.GetString(), out var mtime) ? mtime : (DateTimeOffset?)null,
                sw.TryGetProperty("sha256", out var sh) ? sh.GetString() : null,
                sw.TryGetProperty("apiUpdatedAt", out var au) && DateTimeOffset.TryParse(au.GetString(), out var api) ? api : (DateTimeOffset?)null);
        }

        return new HarnessRefinementRecord
        {
            RefinementId = root.GetProperty("refinementId").GetInt64(),
            Timestamp = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString()!),
            Scope = Enum.Parse<HarnessRefinementScope>(root.GetProperty("scope").GetString()!, ignoreCase: true),
            Kind = Enum.Parse<HarnessRefinementKind>(root.GetProperty("kind").GetString()!, ignoreCase: true),
            Name = root.GetProperty("name").GetString()!,
            Action = Enum.Parse<HarnessRefinementAction>(root.GetProperty("action").GetString()!, ignoreCase: true),
            Version = root.GetProperty("version").GetInt32(),
            Content = root.GetProperty("content").Clone(),
            Author = root.GetProperty("author").GetString()!,
            Evidence = evidence,
            SyncedWith = syncedWith,
            TargetVersion = root.TryGetProperty("targetVersion", out var tv) ? tv.GetInt32() : null,
            Reason = root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            Deleted = root.TryGetProperty("deleted", out var del) && del.GetBoolean(),
        };
    }
}
