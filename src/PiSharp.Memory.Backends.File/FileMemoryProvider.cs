using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Memory.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using IOFile = System.IO.File;
namespace PiSharp.Memory.Backends.File;

/// <summary>
/// JSONL-backed memory backend. Records live in <c>&lt;root&gt;/projects/&lt;projectKey&gt;/records.jsonl</c>
/// (Project scope) and <c>&lt;root&gt;/user/records.jsonl</c> (User scope), one JSON object per line.
/// Summary and mental-model records are mirrored into a diffable <c>memory_summary.md</c> in the same
/// directory. Writes are atomic (temp file + rename); keyword search ranks by token hits in
/// title/tags (2x) and content (1x).
///
/// Reads serve from an in-memory index built once per scope on first access; mutations update the
/// index immediately and mark the scope dirty. A single flush writes <c>records.jsonl</c> (ordered by
/// record key) and regenerates <c>memory_summary.md</c> once per dirty scope. Flushing happens on
/// explicit <see cref="FlushAsync"/>, on dispose (flush-before-return), and as a short debounced
/// background safety net for hosts that never dispose providers. Unflushed writes are lost on hard
/// crash; the loss is bounded by the debounce window.
/// </summary>
public sealed class FileMemoryProvider : IMemoryProvider, IDisposable, IAsyncDisposable
{
    public const string RecordsFileName = "records.jsonl";
    public const string SummaryFileName = "memory_summary.md";

    /// <summary>Background safety-net delay: a dirty scope is flushed this long after its last mutation even if nobody disposes.</summary>
    private static readonly TimeSpan FlushDebounceDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false
    };

    private readonly string _rootDir;
    private readonly string _projectKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;

    // Lazy in-memory index per scope: loaded once on first access, then served without file I/O.
    private readonly Dictionary<MemoryScope, Dictionary<string, MemoryRecord>> _index = new();
    private readonly HashSet<MemoryScope> _dirty = new();

    private readonly CancellationTokenSource _flushCts = new();
    private CancellationTokenSource? _pendingFlush;

    private bool _disposed;

    /// <summary>Number of full records.jsonl rewrites (temp + rename) performed by this instance; diagnostic probe for tests.</summary>
    internal int RecordsWriteCount { get; private set; }

    public FileMemoryProvider(string rootDir, string projectKey, ILogger<FileMemoryProvider>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        _rootDir = rootDir;
        _projectKey = projectKey;
        _logger = logger ?? NullLogger<FileMemoryProvider>.Instance;
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
            ThrowIfDisposed();
            return Index(scope).TryGetValue(recordKey, out var record) ? record : null;
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
            ThrowIfDisposed();
            var records = Index(scope);
            // Idempotent upsert: a stable key keeps its original CreatedAt (birth), only content moves.
            var stored = records.TryGetValue(record.RecordKey, out var existing)
                ? record with { CreatedAt = existing.CreatedAt }
                : record;
            records[record.RecordKey] = stored;
            MarkDirty(scope);
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
            ThrowIfDisposed();
            if (!Index(scope).Remove(recordKey)) return false;
            MarkDirty(scope);
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
            ThrowIfDisposed();
            var records = Index(scope);
            var existing = records.TryGetValue(recordKey, out var current)
                ? current
                : new MemoryRecord(recordKey, MemoryKind.Fact, string.Empty, string.Empty, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var updated = mutate(existing) with { UpdatedAt = DateTimeOffset.UtcNow };
            records[recordKey] = updated;
            MarkDirty(scope);
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
            ThrowIfDisposed();
            return ApplyListFilters(Index(scope).Values, query);
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
            ThrowIfDisposed();
            var tokens = Tokenize(text);
            if (tokens.Count == 0) return [];

            var scored = new List<MemorySearchResult>();
            foreach (var record in Index(scope).Values)
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
    /// <summary>
    /// Persists all pending mutations: writes <c>records.jsonl</c> (temp + rename, ordered by record
    /// key) and regenerates <c>memory_summary.md</c> once per dirty scope.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelPendingFlush();
            await FlushLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Flushes all pending mutations before returning (flush-before-return dispose contract).</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _flushCts.Cancel(); // stop the background safety net; this call flushes synchronously instead.
            await FlushLockedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        _flushCts.Dispose();
    }

    // --- in-memory index ---

    private Dictionary<string, MemoryRecord> Index(MemoryScope scope)
    {
        if (_index.TryGetValue(scope, out var records)) return records;
        records = Load(scope);
        _index[scope] = records; // cached only after a successful load.
        return records;
    }

    private void MarkDirty(MemoryScope scope)
    {
        _dirty.Add(scope);
        ScheduleDebouncedFlush();
    }

    // --- persistence ---

    private Dictionary<string, MemoryRecord> Load(MemoryScope scope)
    {
        var path = RecordsPath(scope);
        if (!IOFile.Exists(path)) return new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);

        var records = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        var lineIndex = 0;
        foreach (var line in IOFile.ReadLines(path))
        {
            lineIndex++;
            try
            {
                var record = JsonSerializer.Deserialize<MemoryRecord>(line, SerializerOptions);
                if (record is null)
                    throw new InvalidDataException("JSONL line deserialized to null.");
                if (string.IsNullOrWhiteSpace(record.RecordKey))
                    throw new InvalidDataException("Memory record is missing a record key.");
                records[record.RecordKey] = record;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Skip-and-log: a single corrupt line (e.g. a torn write or hand edit) must not
                // brick the whole store. The line stays in place; a maintenance pass could rewrite
                // the file without skipped lines (rewrite-if-repaired), but that is not done here.
                _logger.LogWarning(
                    "Skipping corrupt memory record line {LineIndex} in '{Path}': {Reason}",
                    lineIndex, path, ex.Message);
            }
        }
        return records;
    }

    private async Task FlushLockedAsync(CancellationToken ct)
    {
        if (_dirty.Count == 0) return;
        ct.ThrowIfCancellationRequested();

        // Snapshot under the gate: dirty scopes always have a loaded index.
        var scopes = _dirty.ToArray();
        foreach (var scope in scopes)
        {
            var records = _index[scope];
            await WriteRecordsFileAsync(scope, records).ConfigureAwait(false);
            await WriteSummaryFileAsync(scope, records).ConfigureAwait(false);
        }
        _dirty.Clear();
    }

    private async Task WriteRecordsFileAsync(MemoryScope scope, IReadOnlyDictionary<string, MemoryRecord> records)
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
        await IOFile.WriteAllTextAsync(temp, builder.ToString(), Encoding.UTF8).ConfigureAwait(false);
        IOFile.Move(temp, path, overwrite: true);
        RecordsWriteCount++;
    }

    private async Task WriteSummaryFileAsync(MemoryScope scope, IReadOnlyDictionary<string, MemoryRecord> records)
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
        await IOFile.WriteAllTextAsync(temp, builder.ToString(), Encoding.UTF8).ConfigureAwait(false);
        IOFile.Move(temp, path, overwrite: true);
    }

    private string ScopeDir(MemoryScope scope) => scope switch
    {
        MemoryScope.Project => Path.Combine(_rootDir, "projects", _projectKey),
        MemoryScope.User => Path.Combine(_rootDir, "user"),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown memory scope.")
    };

    private string RecordsPath(MemoryScope scope) => Path.Combine(ScopeDir(scope), RecordsFileName);

    // --- debounced background safety net ---

    private void ScheduleDebouncedFlush()
    {
        _pendingFlush?.Cancel();
        _pendingFlush?.Dispose();
        var pending = CancellationTokenSource.CreateLinkedTokenSource(_flushCts.Token);
        _pendingFlush = pending;
        _ = FlushAfterDelayAsync(pending);
    }

    private void CancelPendingFlush()
    {
        _pendingFlush?.Cancel();
        _pendingFlush?.Dispose();
        _pendingFlush = null;
    }

    private async Task FlushAfterDelayAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(FlushDebounceDelay, pending.Token).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer mutation, an explicit flush, or dispose.
        }
        catch (Exception)
        {
            // Best-effort background safety net; failures surface on explicit FlushAsync/Dispose.
        }
        finally
        {
            pending.Dispose(); // idempotent; the scheduler may already have disposed it.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
