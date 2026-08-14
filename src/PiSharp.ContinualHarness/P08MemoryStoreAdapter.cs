using System.Text.Json;
using PiSharp.Memory.Abstractions;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Adapts <see cref="PiSharp.Memory.Abstractions.MemoryServices.Store"/> (P08's process-wide static
/// <see cref="IMemoryStore"/>) to the plugin's <see cref="IHarnessMemoryStore"/> seam. Scope is
/// bound at construction (Local -&gt; Project, Global -&gt; User); record keys are namespaced
/// <c>refine/&lt;name&gt;</c>. The memory store itself stays unversioned — refinement versioning and
/// rollback history remain P09-owned in the journal.
/// </summary>
internal sealed class P08MemoryStoreAdapter : IHarnessMemoryStore
{
    private readonly MemoryScope _scope;

    public P08MemoryStoreAdapter(MemoryScope scope) => _scope = scope;

    public string Describe => $"api:Memory({_scope.ToString().ToLowerInvariant()})";

    public bool IsAvailable
        => MemoryServices.Store is not null;

    private IMemoryStore StoreRequired
        => MemoryServices.Store
           ?? throw new HarnessRejectedException("P08 memory store is not available (no backend wired).");

    public async Task<JsonElement?> GetAsync(string recordKey, CancellationToken ct = default)
    {
        var record = await StoreRequired.GetAsync(_scope, recordKey, ct);
        return record is null ? null : Serialize(record);
    }

    public async Task PutAsync(string recordKey, JsonElement content, CancellationToken ct = default)
    {
        var record = Deserialize(recordKey, content);
        await StoreRequired.PutAsync(_scope, record, ct);
    }

    public async Task DeleteAsync(string recordKey, CancellationToken ct = default)
        => await StoreRequired.DeleteAsync(_scope, recordKey, ct);

    internal static MemoryRecord Deserialize(string recordKey, JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
            throw new HarnessRejectedException("Memory content must be an object with 'title' and 'content' strings.");

        var title = content.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var body = content.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var kind = content.TryGetProperty("kind", out var k) && Enum.TryParse<MemoryKind>(k.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : MemoryKind.Fact;
        var tags = content.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array
            ? tagsEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
            : (IReadOnlyList<string>)[];

        var now = DateTimeOffset.UtcNow;
        return new MemoryRecord(recordKey, kind, title, body, tags, now, now);
    }

    internal static JsonElement Serialize(MemoryRecord record)
        => JsonSerializer.SerializeToElement(new
        {
            recordKey = record.RecordKey,
            kind = record.Kind.ToString().ToLowerInvariant(),
            title = record.Title,
            content = record.Content,
            tags = record.Tags,
        });
}
