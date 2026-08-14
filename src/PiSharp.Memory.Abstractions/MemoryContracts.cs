namespace PiSharp.Memory.Abstractions;

/// <summary>
/// Which memory partition a record belongs to. <see cref="Project"/> records are
/// scoped to the runtime working directory; <see cref="User"/> records are global
/// across all projects.
/// </summary>
public enum MemoryScope
{
    User,
    Project
}

/// <summary>
/// The kind of a memory record. Kinds drive display, prompt injection
/// (mental models) and auto-learn promotion.
/// </summary>
public enum MemoryKind
{
    Fact,
    Lesson,
    Summary,
    MentalModel
}

/// <summary>
/// A single memory record. <see cref="RecordKey"/> is a stable id chosen by the
/// writer (e.g. "facts/oauth-setup"); upserts key on it. <see cref="Relevance"/>
/// is a search-result-only score and is never persisted.
/// </summary>
public sealed record MemoryRecord(
    string RecordKey,
    MemoryKind Kind,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SessionId = null,
    string? TurnId = null,
    float? Relevance = null,
    DateTimeOffset? InvalidatedAt = null)
{
    /// <summary>True when the record was invalidated (auditable soft delete) and is hidden from default searches.</summary>
    public bool IsInvalidated => InvalidatedAt is not null;
}

/// <summary>
/// Filter for listing/recalling records. Backends rank <see cref="Text"/> queries
/// semantically (vector) or by keyword (file/sqlite); kind and tags always filter.
/// </summary>
public sealed record MemoryQuery(
    string? Text = null,
    MemoryKind? Kind = null,
    IReadOnlyList<string>? Tags = null,
    bool IncludeInvalidated = false,
    int Limit = 10);

/// <summary>A record plus its relevance score from <see cref="IMemoryProvider.SearchAsync"/>.</summary>
public sealed record MemorySearchResult(MemoryRecord Record, double Score);

/// <summary>
/// The backend contract implemented by <c>Memory.Off</c>, <c>Memory.File</c>,
/// <c>Memory.Vector</c> and <c>Memory.Sqlite</c>. Scope resolution is the
/// provider's job: providers are constructed for a runtime cwd and map
/// <see cref="MemoryScope.Project"/> to that cwd's project key.
/// </summary>
public interface IMemoryProvider
{
    /// <summary>Stable backend id matching <c>extensions.pisharp-memory.backend</c> ("off" | "file" | "vector" | "sqlite").</summary>
    string Id { get; }

    /// <summary>Human-readable backend name for diagnostics and the /memory command.</summary>
    string DisplayName { get; }

    /// <summary>True when recall uses semantic (embedding) ranking; false for keyword backends.</summary>
    bool SupportsSemanticSearch { get; }

    /// <summary>Returns the record for <paramref name="recordKey"/> in <paramref name="scope"/>, or null when absent.</summary>
    Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default);

    /// <summary>Upserts <paramref name="record"/> keyed by <see cref="MemoryRecord.RecordKey"/>.</summary>
    Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default);

    /// <summary>Hard-deletes the record; returns false when it did not exist.</summary>
    Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the stored record (or a fresh clone when absent)
    /// and persists it, bumping <see cref="MemoryRecord.UpdatedAt"/>. Returns the stored record
    /// after the mutation, or null when the mutation produced a null record.
    /// </summary>
    Task<MemoryRecord?> UpdateAsync(
        MemoryScope scope,
        string recordKey,
        Func<MemoryRecord, MemoryRecord> mutate,
        CancellationToken ct = default);

    /// <summary>Lists records matching <paramref name="query"/> (kind/tags filters).</summary>
    Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default);

    /// <summary>Ranked search: semantic (vector) or keyword (file/sqlite). Never returns invalidated records.</summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default);

    /// <summary>Combined list+search used by the recall tool: keyword/semantic ranking when a text query is present, else filtered list.</summary>
    Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default);
}

/// <summary>
/// The store facade the core plugin exposes for tools and cross-plugin consumers
/// (P09). Project scope binds the provider's project key; user scope is global.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Normalized cwd key for Project scope (e.g. "--C--code--AI--pi--PiSharp--").</summary>
    string ProjectKey { get; }

    /// <summary>The active backend provider.</summary>
    IMemoryProvider Provider { get; }

    Task<MemoryRecord?> GetAsync(MemoryScope scope, string recordKey, CancellationToken ct = default);
    Task PutAsync(MemoryScope scope, MemoryRecord record, CancellationToken ct = default);
    Task<bool> DeleteAsync(MemoryScope scope, string recordKey, CancellationToken ct = default);
    Task<MemoryRecord?> UpdateAsync(
        MemoryScope scope,
        string recordKey,
        Func<MemoryRecord, MemoryRecord> mutate,
        CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string text, int limit = 10, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> RecallAsync(MemoryScope scope, MemoryQuery query, CancellationToken ct = default);
}

/// <summary>
/// Backend registration + lookup. App-base static so every plugin ALC shares one
/// registry (a registry inside a collectible plugin ALC would split-brain across
/// backend plugins). Last registration wins per id.
/// </summary>
public sealed class MemoryProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IMemoryProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) the provider under its <see cref="IMemoryProvider.Id"/>.</summary>
    public void Register(IMemoryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id))
            throw new ArgumentException("Memory provider id must not be empty.", nameof(provider));
        lock (_gate) _providers[provider.Id] = provider;
    }

    /// <summary>Returns the registered provider for <paramref name="id"/>, or null.</summary>
    public IMemoryProvider? TryGet(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate) return _providers.TryGetValue(id, out var provider) ? provider : null;
    }

    /// <summary>Snapshot of all registered providers, in registration order.</summary>
    public IReadOnlyList<IMemoryProvider> All
    {
        get
        {
            lock (_gate) return _providers.Values.ToArray();
        }
    }
}

/// <summary>
/// App-base static locator (single shared instance across all plugin ALCs).
/// <see cref="Providers"/> is filled by backend plugins at InitializeAsync;
/// <see cref="Store"/> is set by the core plugin and consumed by P09.
/// </summary>
public static class MemoryServices
{
    public static MemoryProviderRegistry Providers { get; } = new();

    public static IMemoryStore? Store { get; set; }
}

/// <summary>
/// Project-key encoding shared by backends: the runtime cwd normalized to the
/// same <c>--&lt;encoded-cwd&gt;--</c> convention as session dirs
/// (<see cref="PiSharp.Compatibility.Settings.PiAgentPaths.EncodeCwd"/>).
/// </summary>
public static class MemoryProjectKeys
{
    /// <summary>Encodes <paramref name="cwd"/> into the session-dir convention: leading slashes trimmed, path separators and ':' become '-'.</summary>
    public static string Encode(string cwd)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        return $"--{cwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-')}--";
    }
}
