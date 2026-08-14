using System.Text;
using System.Text.Json;
using PiSharp.ContinualHarness.Contracts;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Thrown when an apply would silently clobber a host edit. Carries the expected (journaled)
/// vs actual (observed-now) sync metadata and a bounded rendered diff.
/// </summary>
public sealed class HarnessConflictException : Exception
{
    public HarnessConflictException(
        HarnessEntryKey key,
        string targetPath,
        HarnessSyncedWith? expected,
        HarnessSyncedWith? actual,
        string diff,
        string? message = null)
        : base(message ?? BuildMessage(key, targetPath, expected, actual))
    {
        Key = key;
        TargetPath = targetPath;
        Expected = expected;
        Actual = actual;
        Diff = diff;
    }

    public HarnessEntryKey Key { get; }
    public string TargetPath { get; }
    public HarnessSyncedWith? Expected { get; }
    public HarnessSyncedWith? Actual { get; }
    public string Diff { get; }

    private static string BuildMessage(HarnessEntryKey key, string targetPath, HarnessSyncedWith? expected, HarnessSyncedWith? actual)
    {
        var expectedStamp = expected is null
            ? "no sync metadata"
            : $"{expected.Sha256 ?? expected.FileMtimeUtc?.ToString("O") ?? expected.Path ?? "?"}";
        var actualStamp = actual is null
            ? "no sync metadata"
            : $"{actual.Sha256 ?? actual.FileMtimeUtc?.ToString("O") ?? actual.Path ?? "?"}";
        return $"Harness conflict on '{key}' at {targetPath}: target changed since last sync (expected {expectedStamp}, actual {actualStamp}). Refuse to clobber unless forced.";
    }
}

/// <summary>
/// Raised for non-conflict rejection reasons (disallowed kind, missing evidence, scope policy).
/// </summary>
public sealed class HarnessRejectedException(string message) : Exception(message)
{
}

/// <summary>
/// Per-scope journal: load/replay/append with deterministic effective-state derivation. Append is
/// atomic (temp-file + <see cref="File.Move(overwrite: true)"/>) and serialized by an in-process
/// gate, mirroring P02's state-store conventions.
/// </summary>
public sealed class HarnessStore
{
    public const string JournalType = "harness-refinements";
    public const int JournalSchemaVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<HarnessRefinementRecord> _records = [];
    private readonly Dictionary<HarnessEntryKey, HarnessEntry> _effective = new();
    private readonly Dictionary<HarnessEntryKey, List<HarnessRefinementRecord>> _history = new();

    public HarnessStore(HarnessRefinementScope scope, string journalPath)
    {
        Scope = scope;
        JournalPath = journalPath;
    }

    public HarnessRefinementScope Scope { get; }
    public string JournalPath { get; }

    /// <summary>All records in replay order.</summary>
    public IReadOnlyList<HarnessRefinementRecord> Records => _records;

    /// <summary>The live effective-entry map (excludes tombstones).</summary>
    public IReadOnlyDictionary<HarnessEntryKey, HarnessEntry> Effective => _effective;

    public HarnessEntry? Get(HarnessEntryKey key)
        => _effective.TryGetValue(key, out var entry) ? entry : null;

    public IReadOnlyList<HarnessRefinementRecord> History(HarnessEntryKey key)
        => _history.TryGetValue(key, out var history) ? history : [];

    /// <summary>Returns the record that produced <paramref name="version"/> for <paramref name="key"/>, or null.</summary>
    public HarnessRefinementRecord? At(HarnessEntryKey key, int version)
    {
        if (!_history.TryGetValue(key, out var history)) return null;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Version == version) return history[i];
        }
        return null;
    }

    /// <summary>Loads/replays the journal from disk (missing file yields an empty store).</summary>
    public HarnessStore Load(CancellationToken ct = default)
    {
        if (!File.Exists(JournalPath))
        {
            _records.Clear();
            _effective.Clear();
            _history.Clear();
            return this;
        }

        var lines = File.ReadAllLines(JournalPath);
        if (lines.Length == 0) return this;

        var headerIssue = HarnessJournalFormat.ParseHeader(lines[0], out _);
        if (headerIssue is not null)
            throw new InvalidDataException($"Invalid {nameof(HarnessStore)} journal '{JournalPath}': {headerIssue}");

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var record = HarnessJournalFormat.DeserializeRecord(lines[i]);
            ApplyRecord(record);
        }
        return this;
    }

    /// <summary>
    /// Appends a record atomically and replays it into the in-memory view. Assigns the
    /// <see cref="HarnessRefinementRecord.RefinementId"/> (monotonic, 1-based).
    /// </summary>
    public async Task<long> AppendAsync(HarnessRefinementRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(record);

            var nextId = _records.Count == 0
                ? 1L
                : (_records.Max(r => r.RefinementId) + 1);

            var withId = record with { RefinementId = nextId };

            var dir = Path.GetDirectoryName(JournalPath)!;
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            if (!File.Exists(JournalPath))
                sb.Append(HarnessJournalFormat.WriteHeader(Scope, Environment.CurrentDirectory)).Append('\n');

            sb.Append(HarnessJournalFormat.SerializeRecord(withId)).Append('\n');

            var existing = File.Exists(JournalPath) ? File.ReadAllText(JournalPath) : string.Empty;
            var tempPath = JournalPath + ".tmp";
            File.WriteAllText(tempPath, existing + sb);
            File.Move(tempPath, JournalPath, overwrite: true);

            ApplyRecord(withId);
            return nextId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-bases the in-memory view of an entry onto content read back from the target (a host edit
    /// winning re-sync). Marks the entry dirty. Does not touch the journal.
    /// </summary>
    public HarnessEntry ApplyResync(HarnessEntryKey key, JsonElement content, HarnessSyncedWith syncedWith)
    {
        var current = _effective.TryGetValue(key, out var existing) ? existing : null;
        var version = current?.Version ?? 1;
        var lastRefinementId = current?.LastRefinementId ?? 0;
        var updatedAt = current?.UpdatedAt ?? DateTimeOffset.UtcNow;

        var resynced = new HarnessEntry
        {
            Key = key,
            Version = version,
            Content = content.Clone(),
            Scope = current?.Scope ?? Scope,
            UpdatedAt = updatedAt,
            LastRefinementId = lastRefinementId,
            SyncedWith = syncedWith,
            Dirty = true,
        };
        _effective[key] = resynced;

        if (!_history.TryGetValue(key, out var history))
        {
            history = [];
            _history[key] = history;
        }
        return resynced;
    }

    private void ApplyRecord(HarnessRefinementRecord record)
    {
        var key = record.Key;
        _records.Add(record);

        if (!_history.TryGetValue(key, out var history))
        {
            history = [];
            _history[key] = history;
        }
        history.Add(record);

        if (record.Action == HarnessRefinementAction.Delete)
        {
            _effective.Remove(key);
            return;
        }

        _effective[key] = new HarnessEntry
        {
            Key = key,
            Version = record.Version,
            Content = record.Content.Clone(),
            Scope = record.Scope,
            UpdatedAt = record.Timestamp,
            LastRefinementId = record.RefinementId,
            SyncedWith = record.SyncedWith,
        };
    }
}
