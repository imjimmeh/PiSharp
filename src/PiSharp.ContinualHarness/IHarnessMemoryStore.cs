using System.Text.Json;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Minimal seam over the P08 memory store (P08-spec: static <c>MemoryServices.Store</c>; upsert by
/// <see cref="RecordKey"/>; unversioned). P08's static locator is not yet wireable from a plugin
/// ALC without a registry, so P09 defines this narrow adapter and the extension wires a real
/// implementation when one is available. The store itself stays unversioned — all refinement
/// version/rollback history is P09-owned in the journal.
/// </summary>
public interface IHarnessMemoryStore
{
    /// <summary>Human-readable target description (e.g. "api:-Memory-").</summary>
    string Describe { get; }

    /// <summary>Reads the current content for <paramref name="recordKey"/>, or null when absent.</summary>
    Task<JsonElement?> GetAsync(string recordKey, CancellationToken ct = default);

    /// <summary>Upserts the content for <paramref name="recordKey"/>.</summary>
    Task PutAsync(string recordKey, JsonElement content, CancellationToken ct = default);

    /// <summary>Deletes <paramref name="recordKey"/>.</summary>
    Task DeleteAsync(string recordKey, CancellationToken ct = default);
}
