using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Memory.Abstractions;
using IOFile = System.IO.File;
namespace PiSharp.Memory.Backends.File;

/// <summary>
/// JSONL-backed memory backend. Records live in <c>&lt;root&gt;/projects/&lt;projectKey&gt;/records.jsonl</c>
/// (Project scope) and <c>&lt;root&gt;/user/records.jsonl</c> (User scope), one JSON object per line.
/// Summary and mental-model records are mirrored into a diffable <c>memory_summary.md</c> in the same
/// directory. Writes are serialized and atomic (temp file + rename); keyword search ranks by token hits
/// in title/tags (2x) and content (1x).
/// </summary>
public sealed class FileMemoryProvider : IMemoryProvider
{
    public const string RecordsFileName = "records.jsonl";
    public const string SummaryFileName = "memory_summary.md";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false
    };

    private readonly string _rootDir;
    private readonly string _projectKey;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileMemoryProvider(string rootDir, string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        _rootDir = rootDir;
        _projectKey = projectKey;
    }

    public string Id => "file";
    public string DisplayName => "File (JSONL + memory_summary.md)";
    public bool SupportsSemanticSearch => false;

    public async Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = Load(scope);
            return records.TryGetValue(recordKey, out var record) ? record : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RecordKey);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = Load(scope);
            // Idempotent upsert: a stable key keeps its original CreatedAt (birth), only content moves.
            var stored = records.TryGetValue(record.RecordKey, out var existing)
                ? record with { CreatedAt = existing.CreatedAt }
                : record;
            records[record.RecordKey] = stored;
            Save(scope, records);
            RegenerateSummary(scope, records);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = Load(scope);
            if (!records.Remove(recordKey)) return false;
            Save(scope, records);
            RegenerateSummary(scope, records);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemoryRecord?> UpdateAsync(
        MemoryScope scope,
        string recordKey,
        Func<MemoryRecord, MemoryRecord> mutate,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        ArgumentNullException.ThrowIfNull(mutate);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = Load(scope);
            var existing = records.TryGetValue(recordKey, out var current)
                ? current
                : new MemoryRecord(recordKey, MemoryKind.Fact, string.Empty, string.Empty, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var updated = mutate(existing) with { UpdatedAt = DateTimeOffset.UtcNow };
            records[recordKey] = updated;
            Save(scope, records);
            RegenerateSummary(scope, records);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }


    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return ApplyListFilters(Load(scope).Values, query);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (limit <= 0) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = Tokenize(text);
            if (tokens.Count == 0) return [];

            var scored = new List<MemorySearchResult>();
            foreach (var record in Load(scope).Values)
            {
                if (record.IsInvalidated) continue;
                var score = Score(record, tokens);
                if (score > 0) scored.Add(new MemorySearchResult(record, score));
            }
            return scored
                .OrderByDescending(result => result.Score)
                .ThenByDescending(result => result.Record.UpdatedAt)
                .Take(limit)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text))
            return await ListAsync(scope, query, ct).ConfigureAwait(false);

        var limit = query.Limit <= 0 ? 10 : query.Limit;
        var results = await SearchAsync(scope, query.Text, limit: limit, ct).ConfigureAwait(false);
        return ApplyListFilters(results.Select(result => result.Record), query);
    }

    private static IReadOnlyList<MemoryRecord> ApplyListFilters(IEnumerable<MemoryRecord> records, MemoryQuery query)
    {
        var limit = query.Limit <= 0 ? 10 : query.Limit;
        IEnumerable<MemoryRecord> filtered = records;
        if (query.Kind is { } kind) filtered = filtered.Where(record => record.Kind == kind);
        if (query.Tags is { Count: > 0 } tags)
            filtered = filtered.Where(record => tags.All(record.Tags.Contains));
        if (!query.IncludeInvalidated) filtered = filtered.Where(record => !record.IsInvalidated);
        return filtered
            .OrderByDescending(record => record.UpdatedAt)
            .Take(limit)
            .ToArray();
    }

    // --- persistence ---

    private Dictionary<string, MemoryRecord> Load(MemoryScope scope)
    {
        var path = RecordsPath(scope);
        if (!IOFile.Exists(path)) return new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);

        var records = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        foreach (var line in IOFile.ReadLines(path))
            try
            {
                var record = JsonSerializer.Deserialize<MemoryRecord>(line, SerializerOptions);
                if (record is null)
                    throw new InvalidDataException($"Corrupt memory record line in '{path}'.");
                records[record.RecordKey] = record;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Corrupt memory record line in '{path}': {exception.Message}", exception);
            }
        return records;
    }

    private void Save(MemoryScope scope, IReadOnlyDictionary<string, MemoryRecord> records)
    {
        var dir = ScopeDir(scope);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, RecordsFileName);

        var builder = new StringBuilder();
        foreach (var record in records.Values.OrderBy(record => record.RecordKey, StringComparer.Ordinal))
        {
            builder.AppendLine(JsonSerializer.Serialize(record, SerializerOptions));
        }

        var temp = path + ".tmp";
        IOFile.WriteAllText(temp, builder.ToString(), Encoding.UTF8);
        IOFile.Move(temp, path, overwrite: true);
    }

    private void RegenerateSummary(MemoryScope scope, IReadOnlyDictionary<string, MemoryRecord> records)
    {
        var dir = ScopeDir(scope);
        var path = Path.Combine(dir, SummaryFileName);
        var summarized = records.Values
            .Where(record => !record.IsInvalidated && record.Kind is MemoryKind.Summary or MemoryKind.MentalModel)
            .OrderByDescending(record => record.UpdatedAt)
            .ToArray();

        if (summarized.Length == 0)
        {
            if (IOFile.Exists(path)) IOFile.Delete(path);
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(scope == MemoryScope.Project
            ? $"# Project memory ({_projectKey})"
            : "# User memory");
        builder.AppendLine();
        foreach (var record in summarized)
        {
            var oneLine = record.Content.Replace('\n', ' ').Trim();
            builder.AppendLine($"- [{record.RecordKey}] {record.Title} (updated {record.UpdatedAt:yyyy-MM-dd}): {oneLine}");
        }

        var temp = path + ".tmp";
        IOFile.WriteAllText(temp, builder.ToString(), Encoding.UTF8);
        IOFile.Move(temp, path, overwrite: true);
    }

    private string ScopeDir(MemoryScope scope) => scope switch
    {
        MemoryScope.Project => Path.Combine(_rootDir, "projects", _projectKey),
        MemoryScope.User => Path.Combine(_rootDir, "user"),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown memory scope.")
    };

    private string RecordsPath(MemoryScope scope) => Path.Combine(ScopeDir(scope), RecordsFileName);

    // --- keyword scoring ---

    private static IReadOnlyList<string> Tokenize(string text)
        => text.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_', '=', '+', '\'', '"', '`', '#', '@', '$', '%', '^', '&', '*', '|', '<', '>', '~'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double Score(MemoryRecord record, IReadOnlyList<string> tokens)
    {
        var title = record.Title.ToLowerInvariant();
        var content = record.Content.ToLowerInvariant();
        var tags = record.Tags.Select(tag => tag.ToLowerInvariant()).ToArray();

        double score = 0;
        foreach (var token in tokens)
        {
            if (title.Contains(token, StringComparison.Ordinal)) score += 2;
            if (tags.Any(tag => tag.Contains(token, StringComparison.Ordinal))) score += 2;
            if (content.Contains(token, StringComparison.Ordinal)) score += 1;
        }
        return score;
    }
}
