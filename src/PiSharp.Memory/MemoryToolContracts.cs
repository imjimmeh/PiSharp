using System.ComponentModel;

namespace PiSharp.Memory;

/// <summary>Input for the <c>retain</c> tool: upsert a memory record.</summary>
public sealed record RetainToolInput(
    [property: Description("Scope: \"user\" (global) or \"project\" (default, scoped to the working directory).")] string? Scope,
    [property: Description("Kind: \"fact\" (default), \"lesson\", \"summary\", \"mental-model\".")] string? Kind,
    [property: Description("Short title of the record.")] string Title,
    [property: Description("Body content of the record.")] string Content,
    [property: Description("Optional tags for filtering and search.")] string[]? Tags,
    [property: Description("Stable record key for idempotent updates, e.g. \"facts/oauth-setup\". Defaults to a slugified kind/timestamp key.")] string? RecordKey);

/// <summary>Input for the <c>recall</c> tool: ranked memory lookup.</summary>
public sealed record RecallToolInput(
    [property: Description("Scope: \"user\" or \"project\" (default).")] string? Scope,
    [property: Description("Free-text query; keyword-ranked on file/sqlite, semantic on vector. Omit to list records instead.")] string? Query,
    [property: Description("Filter by kind: \"fact\", \"lesson\", \"summary\", \"mental-model\".")] string? Kind,
    [property: Description("Filter by tags; all listed tags must match.")] string[]? Tags,
    [property: Description("Maximum results (default 10, max 100).")] int? Limit,
    [property: Description("Include invalidated records (default false).")] bool? IncludeInvalidated);

/// <summary>
/// Input for the <c>reflect</c> tool: read-only synthesis. Gathers matching
/// records (and recent session context when available) for the model to
/// synthesize in its reply; the model then calls <c>retain</c> with
/// <c>kind = "summary"</c>. Never writes.
/// </summary>
public sealed record ReflectToolInput(
    [property: Description("Scope: \"user\" or \"project\" (default).")] string? Scope,
    [property: Description("Optional topic to focus the reflection.")] string? Topic,
    [property: Description("Include recent session context in the synthesis material (default true).")] bool? IncludeContext);

/// <summary>Input for the <c>memory_edit</c> tool: partial update or invalidation of a record.</summary>
public sealed record MemoryEditToolInput(
    [property: Description("Scope: \"user\" or \"project\" (default).")] string? Scope,
    [property: Description("Record key to edit, e.g. \"facts/oauth-setup\".")] string RecordKey,
    [property: Description("New title; omitted keeps the current title.")] string? Title,
    [property: Description("New content; omitted keeps the current content.")] string? Content,
    [property: Description("New tags; omitted keeps the current tags.")] string[]? Tags,
    [property: Description("Invalidate instead of updating: hides the record from default searches (default false).")] bool? Invalidate);

/// <summary>Input for the <c>learn</c> tool: store a reusable lesson, optionally promoting it to a managed skill.</summary>
public sealed record LearnToolInput(
    [property: Description("Short lesson title.")] string Title,
    [property: Description("The reusable lesson body.")] string Lesson,
    [property: Description("Optional tags for filtering and search.")] string[]? Tags,
    [property: Description("Scope: \"user\" or \"project\" (default).")] string? Scope,
    [property: Description("Promote the lesson to a managed skill (default false). Requires a managed-skill store.")] bool? Promote,
    [property: Description("Skill name for promotion; required when promote is true.")] string? SkillName,
    [property: Description("Skill description for promotion.")] string? SkillDescription);

/// <summary>
/// Structured tool details returned with every memory tool result so clients can
/// render/route without parsing markdown. <see cref="Blocked"/> marks the
/// "backend is off" gate; <see cref="Error"/> marks a failed execution.
/// </summary>
public sealed record MemoryToolDetails(
    string Tool,
    bool Blocked = false,
    bool Error = false,
    string? ErrorMessage = null,
    string? RecordKey = null,
    int? Count = null,
    string? Backend = null,
    string? Warning = null,
    object? Extra = null);
