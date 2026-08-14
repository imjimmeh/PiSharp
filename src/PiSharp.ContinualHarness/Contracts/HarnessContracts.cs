using System.Text.Json;

namespace PiSharp.ContinualHarness.Contracts;

/// <summary>
/// The harness state a refinement targets. <see cref="Prompt"/> and <see cref="Memory"/>
/// refinements are journal-backed; <see cref="Skill"/> extends P04's managed-skill store;
/// <see cref="Subagent"/> writes P06-format agent-definition files.
/// </summary>
public enum HarnessRefinementKind { Prompt, Memory, Skill, Subagent }

/// <summary>
/// The state transition a refinement record applies. <see cref="Rollback"/> restores a prior
/// version's content through the normal write path and is itself versioned and auditable.
/// </summary>
public enum HarnessRefinementAction { Create, Update, Delete, Rollback }

/// <summary>
/// Which journal a refinement persists to: <see cref="Local"/> -> the project journal
/// (<c>&lt;cwd&gt;/.pi/PiSharp/harness/refinements.jsonl</c>), <see cref="Global"/> -> the user
/// journal (<c>~/.pi/PiSharp/harness/refinements.jsonl</c>).
/// </summary>
public enum HarnessRefinementScope { Local, Global }

/// <summary>
/// Stable identity of a harness entry: a kind plus a slug name. Memory records namespace their
/// backing <c>RecordKey</c> as <c>refine/&lt;name&gt;</c> internally.
/// </summary>
public sealed record HarnessEntryKey(HarnessRefinementKind Kind, string Name)
{
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}/{Name}";
}

/// <summary>
/// A bounded citation tying a refinement to what the author observed. <see cref="EntryId"/>
/// is populated from the live session when the host exposes binding (optional core change A1);
/// v1 records model/user-cited ids via <see cref="Excerpt"/>.
/// </summary>
public sealed record RefinementEvidence(string SessionId, string? EntryId = null, string? Excerpt = null);

/// <summary>
/// Metadata captured when the daemon last read/wrote a refinement target, used for clobber
/// protection. File-backed targets (subagent .md) carry a path + mtime + content hash; API-backed
/// targets (P04/P08) carry the API-observed update time/content hash instead.
/// </summary>
public sealed record HarnessSyncedWith(
    string? Path = null,
    DateTimeOffset? FileMtimeUtc = null,
    string? Sha256 = null,
    DateTimeOffset? ApiUpdatedAt = null);

/// <summary>
/// An append-only journal record. Every record embeds the full content payload it applies so any
/// version is restorable (rollback snapshots without separate snapshot files).
/// </summary>
public sealed record HarnessRefinementRecord
{
    /// <summary>Monotonic per-journal id (1-based).</summary>
    public required long RefinementId { get; init; }

    /// <summary>When the record was created.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The journal scope this record lives in.</summary>
    public required HarnessRefinementScope Scope { get; init; }

    /// <summary>Which harness state is refined.</summary>
    public required HarnessRefinementKind Kind { get; init; }

    /// <summary>Entry slug name.</summary>
    public required string Name { get; init; }

    /// <summary>The transition applied.</summary>
    public required HarnessRefinementAction Action { get; init; }

    /// <summary>Entry version after this record applies (1-based).</summary>
    public required int Version { get; init; }

    /// <summary>The kind payload; delete carries the last-known content.</summary>
    public required JsonElement Content { get; init; }

    /// <summary>"user", "model", the session id, or "system".</summary>
    public required string Author { get; init; }

    /// <summary>Citations into the transcript that motivated the change.</summary>
    public IReadOnlyList<RefinementEvidence> Evidence { get; init; } = [];

    /// <summary>Target sync metadata captured at apply time.</summary>
    public HarnessSyncedWith? SyncedWith { get; init; }

    /// <summary>Version restored, when <see cref="Action"/> == Rollback.</summary>
    public int? TargetVersion { get; init; }

    /// <summary>Optional human-readable justification.</summary>
    public string? Reason { get; init; }

    /// <summary>Tombstone (true when <see cref="Action"/> == Delete).</summary>
    public bool Deleted { get; init; }

    /// <summary>The entry this record applies to.</summary>
    public HarnessEntryKey Key => new(Kind, Name);
}

/// <summary>
/// The live (effective) view of a harness entry after journal replay, optionally overridden by a
/// host-edit re-sync (which marks it <see cref="Dirty"/> without writing to the journal).
/// </summary>
public sealed record HarnessEntry
{
    public required HarnessEntryKey Key { get; init; }
    public required int Version { get; init; }
    public required JsonElement Content { get; init; }
    public required HarnessRefinementScope Scope { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required long LastRefinementId { get; init; }
    public HarnessSyncedWith? SyncedWith { get; init; }
    public bool Dirty { get; init; }
}
