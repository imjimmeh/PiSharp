using System.Text.Json;
using PiSharp.ContinualHarness;
using PiSharp.ContinualHarness.Contracts;
using Xunit;

namespace PiSharp.ContinualHarness.Tests;

public sealed class HarnessStoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ch-store-" + Guid.NewGuid().ToString("N"));

    public HarnessStoreTests() => Directory.CreateDirectory(_temp);
    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static HarnessRefinementRecord Record(HarnessRefinementScope scope, HarnessRefinementKind kind, HarnessRefinementAction action, string name, int version, string markdown, long id = 0)
        => new()
        {
            RefinementId = id,
            Timestamp = DateTimeOffset.UtcNow,
            Scope = scope,
            Kind = kind,
            Name = name,
            Action = action,
            Version = version,
            Content = JsonSerializer.SerializeToElement(new { markdown }),
            Author = "user",
        };

    [Fact]
    public async Task RoundTrips_Across_Instances()
    {
        var path = Path.Combine(_temp, "refinements.jsonl");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "coding", version: 1, markdown: "v1"));
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Update, name: "coding", version: 2, markdown: "v2"));

        var reloaded = new HarnessStore(HarnessRefinementScope.Local, path).Load();
        Assert.Equal(2, reloaded.Records.Count);
        Assert.Single(reloaded.Effective);
        var entry = reloaded.Get(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding"));
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Version);
        Assert.Equal("v2", entry.Content.GetProperty("markdown").GetString());
        Assert.Equal(2, reloaded.History(new HarnessEntryKey(HarnessRefinementKind.Prompt, "coding")).Count);
    }

    [Fact]
    public void Rejects_Bad_Header_Type()
    {
        var path = Path.Combine(_temp, "bad.jsonl");
        File.WriteAllText(path, "{\"type\":\"something-else\",\"version\":1}\n");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        var ex = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Contains("type", ex.Message);
    }

    [Fact]
    public void Rejects_Bad_Header_Version()
    {
        var path = Path.Combine(_temp, "bad.jsonl");
        File.WriteAllText(path, "{\"type\":\"harness-refinements\",\"version\":99}\n");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        var ex = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public async Task Effective_State_Replay_With_Delete_Tombstone()
    {
        var path = Path.Combine(_temp, "refinements.jsonl");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        var key = new HarnessEntryKey(HarnessRefinementKind.Prompt, "guide");
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "guide", version: 1, markdown: "a"));
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Update, name: "guide", version: 2, markdown: "b"));
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Delete, name: "guide", version: 3, markdown: "b", id: 0));

        Assert.Null(store.Get(key));                       // deleted -> not effective
        Assert.Equal(3, store.History(key).Count);          // but history retained
        Assert.Empty(store.Effective.Keys.Where(k => k.Equals(key)));
    }

    [Fact]
    public async Task Versions_Are_Monotonic_Across_Create_Update()
    {
        var path = Path.Combine(_temp, "refinements.jsonl");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "x", version: 1, markdown: "1"));
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Update, name: "x", version: 2, markdown: "2"));
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Update, name: "x", version: 3, markdown: "3"));

        var history = store.History(new HarnessEntryKey(HarnessRefinementKind.Prompt, "x"));
        Assert.Equal(new[] { 1, 2, 3 }, history.Select(r => r.Version));
        Assert.Equal(1, store.At(new HarnessEntryKey(HarnessRefinementKind.Prompt, "x"), 1)!.Version);
    }

    [Fact]
    public async Task Append_Is_Atomic_No_Partial_Line_No_Temp_Left()
    {
        var path = Path.Combine(_temp, "refinements.jsonl");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "a", version: 1, markdown: "a"));
        await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "b", version: 1, markdown: "b"));

        var reloaded = new HarnessStore(HarnessRefinementScope.Local, path).Load();
        Assert.Equal(2, reloaded.Records.Count);

        // No temp files left behind.
        Assert.Empty(Directory.GetFiles(_temp, "*.tmp"));
        // Every line (after header) parses as a record.
        var lines = File.ReadAllLines(path);
        Assert.All(lines.Skip(1), line => Assert.NotNull(HarnessJournalFormat.DeserializeRecord(line)));
    }

    [Fact]
    public async Task RefinementIds_Are_Monotonic()
    {
        var path = Path.Combine(_temp, "refinements.jsonl");
        var store = new HarnessStore(HarnessRefinementScope.Local, path);
        var id1 = await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "a", version: 1, markdown: "a"));
        var id2 = await store.AppendAsync(Record(scope: HarnessRefinementScope.Local, kind: HarnessRefinementKind.Prompt, action: HarnessRefinementAction.Create, name: "b", version: 1, markdown: "b"));
        Assert.Equal(1, id1);
        Assert.Equal(2, id2);
        Assert.Equal(new long[] { 1, 2 }, store.Records.Select(r => r.RefinementId));
    }
}
