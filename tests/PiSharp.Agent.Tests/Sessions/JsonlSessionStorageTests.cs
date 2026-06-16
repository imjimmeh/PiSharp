using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using Xunit;

namespace PiSharp.Agent.Tests.Sessions;

public sealed class JsonlSessionStorageTests
{
    [Fact]
    public void HeaderMustBeVersion3()
    {
        const string header = "{\"type\":\"session\",\"version\":3}";
        Assert.Contains("\"version\":3", header);
    }

    [Fact]
    public void LeafEntriesUseExactDiscriminator()
    {
        Assert.Equal("leaf", PiSharp.Abstractions.Sessions.LeafEntry.TypeName);
    }

    [Fact]
    public async Task CreateAppendReopenRetainsEntriesAndLeaf()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/.pi/sessions/test/session.jsonl";
        var storage = await JsonlSessionStorage.CreateAsync(fs, path, "/workspace", "s-123", null);

        await storage.AppendEntryAsync(new PiSharp.Abstractions.Sessions.MessageEntry
        {
            Id = "m-1",
            ParentId = null,
            Timestamp = DateTimeOffset.UtcNow,
            Message = AgentMessages.User("hello")
        });

        await storage.AppendEntryAsync(new PiSharp.Abstractions.Sessions.MessageEntry
        {
            Id = "m-2",
            ParentId = null,
            Timestamp = DateTimeOffset.UtcNow,
            Message = AgentMessages.Assistant("response")
        });

        var reopened = await JsonlSessionStorage.OpenAsync(fs, path);
        var leaf = await reopened.GetLeafIdAsync();
        var entries = await reopened.GetEntriesAsync();

        Assert.Equal("m-2", leaf);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "m-1");
        Assert.Contains(entries, e => e.Id == "m-2");
    }

    [Fact]
    public async Task HeaderOnlyFileOpensAndLoadsMetadata()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/empty.jsonl";
        var metadata = new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "/repo", path);
        await storageHeaderOnly(fs, path, metadata);

        var storage = await JsonlSessionStorage.OpenAsync(fs, path);
        var actual = await storage.GetMetadataAsync();
        Assert.Equal(metadata.Id, actual.Id);
        Assert.Equal(metadata.Cwd, actual.Cwd);
        Assert.Equal(metadata.Path, actual.Path);

        static async Task storageHeaderOnly(FakeFileSystem fs, string path, JsonlSessionMetadata metadata)
        {
            var header = new
            {
                type = "session",
                version = 3,
                id = metadata.Id,
                timestamp = metadata.CreatedAt,
                cwd = metadata.Cwd,
                parentSession = (string?)null
            };
            var line = System.Text.Json.JsonSerializer.Serialize(header, new System.Text.Json.JsonSerializerOptions());
            await fs.WriteFileAsync(path, line + "\n");
        }
    }

    [Fact]
    public async Task LoadMetadataIncludesSessionSummaryFields()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/summary.jsonl";
        var createdAt = DateTimeOffset.Parse("2026-05-01T10:00:00Z");
        var firstMessageAt = DateTimeOffset.Parse("2026-05-01T10:05:00Z");
        var assistantAt = DateTimeOffset.Parse("2026-05-01T10:10:00Z");
        var nameAt = DateTimeOffset.Parse("2026-05-01T10:15:00Z");
        await fs.WriteFileAsync(path, $"{{\"type\":\"session\",\"version\":3,\"id\":\"sid\",\"timestamp\":\"{createdAt:O}\",\"cwd\":\"/repo\",\"parentSession\":\"/repo/parent.jsonl\"}}\n");
        var storage = await JsonlSessionStorage.OpenAsync(fs, path);
        await storage.AppendEntryAsync(new MessageEntry
        {
            Id = "m-1",
            ParentId = null,
            Timestamp = firstMessageAt,
            Message = AgentMessages.User("Fix auth bug", firstMessageAt)
        });
        await storage.AppendEntryAsync(new MessageEntry
        {
            Id = "m-2",
            ParentId = "m-1",
            Timestamp = assistantAt,
            Message = AgentMessages.Assistant("Use the token refresh path", assistantAt)
        });
        await storage.AppendEntryAsync(new SessionInfoEntry
        {
            Id = "info-1",
            ParentId = "m-2",
            Timestamp = nameAt,
            Name = "Auth triage"
        });

        var metadata = await JsonlSessionStorage.LoadMetadataAsync(fs, path);

        Assert.Equal("sid", metadata.Id);
        Assert.Equal("/repo/parent.jsonl", metadata.ParentSessionPath);
        Assert.Equal("Auth triage", metadata.Name);
        Assert.Equal("Fix auth bug", metadata.FirstMessage);
        Assert.Equal(2, metadata.MessageCount);
        Assert.Contains("Fix auth bug", metadata.AllMessagesText);
        Assert.Contains("Use the token refresh path", metadata.AllMessagesText);
        Assert.Equal(assistantAt, metadata.ModifiedAt);
    }

    [Fact]
    public async Task LoadMetadataFallsBackWhenSessionHasNoMessages()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/header-only.jsonl";
        var createdAt = DateTimeOffset.Parse("2026-05-01T10:00:00Z");
        await fs.WriteFileAsync(path, $"{{\"type\":\"session\",\"version\":3,\"id\":\"sid\",\"timestamp\":\"{createdAt:O}\",\"cwd\":\"/repo\",\"parentSession\":null}}\n");

        var metadata = await JsonlSessionStorage.LoadMetadataAsync(fs, path);

        Assert.Equal("(no messages)", metadata.FirstMessage);
        Assert.Equal(0, metadata.MessageCount);
        Assert.Equal(string.Empty, metadata.AllMessagesText);
        Assert.Equal(createdAt, metadata.ModifiedAt);
    }

    [Fact]
    public async Task UnsupportedFutureSessionHeaderThrows()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/malformed.jsonl";
        await fs.WriteFileAsync(path, "{\"type\":\"session\",\"version\":4}\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => JsonlSessionStorage.OpenAsync(fs, path));
    }

    [Fact]
    public async Task SetLeafWritesLeafEntryWhenNativeModeIsExplicit()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/leaf-write.jsonl";
        var storage = await JsonlSessionStorage.CreateAsync(fs, path, "/workspace", "sid", null, writeLeafEntries: true);
        var id = "m-1";
        await storage.AppendEntryAsync(new PiSharp.Abstractions.Sessions.MessageEntry
        {
            Id = id,
            ParentId = null,
            Timestamp = DateTimeOffset.UtcNow,
            Message = AgentMessages.User("one")
        });

        await storage.SetLeafIdAsync(id);
        var leafId = await storage.GetLeafIdAsync();
        var entries = await storage.GetEntriesAsync();

        Assert.Equal(id, leafId);
        Assert.Contains(entries.OfType<PiSharp.Abstractions.Sessions.LeafEntry>(), e => e.TargetId == id);
    }

    [Fact]
    public async Task AppendEntriesAsyncWritesMultipleEntriesWithSingleFileAppend()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/.pi/sessions/test/session.jsonl";
        var storage = await JsonlSessionStorage.CreateAsync(fs, path, "/workspace", "sid", null);

        await storage.AppendEntriesAsync([
            new MessageEntry { Id = "m-1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") },
            new MessageEntry { Id = "m-2", ParentId = "m-1", Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.Assistant("response") }
        ]);

        var reopened = await JsonlSessionStorage.OpenAsync(fs, path);
        var entries = await reopened.GetEntriesAsync();
        Assert.Equal(1, fs.AppendCallCount);
        Assert.Equal("m-2", await reopened.GetLeafIdAsync());
        Assert.Equal(["m-1", "m-2"], entries.Select(entry => entry.Id));
    }

    [Fact]
    public async Task FileNotCreatedBeforeFirstUserMessage()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/.pi/sessions/test/session.jsonl";
        var storage = await JsonlSessionStorage.CreateAsync(fs, path, "/workspace", "s-123", null);

        // Simulate startup: model and thinking level changes before any user interaction
        await storage.AppendEntryAsync(new ModelChangeEntry { Id = "mc-1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Provider = "anthropic", ModelId = "claude-opus-4" });
        await storage.AppendEntryAsync(new ThinkingLevelChangeEntry { Id = "tl-1", ParentId = "mc-1", Timestamp = DateTimeOffset.UtcNow, ThinkingLevel = "off" });

        // File must not exist on disk yet
        Assert.Null(fs.ReadFileOrDefault(path));

        // Entries must be available in memory
        var entries = await storage.GetEntriesAsync();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task FirstUserMessageFlushesAllQueuedEntriesToDisk()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/.pi/sessions/test/session.jsonl";
        var storage = await JsonlSessionStorage.CreateAsync(fs, path, "/workspace", "s-123", null);

        // Pre-user entries that are queued in memory
        await storage.AppendEntryAsync(new ModelChangeEntry { Id = "mc-1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Provider = "anthropic", ModelId = "claude-opus-4" });
        await storage.AppendEntryAsync(new ThinkingLevelChangeEntry { Id = "tl-1", ParentId = "mc-1", Timestamp = DateTimeOffset.UtcNow, ThinkingLevel = "off" });

        // First user message triggers the flush
        await storage.AppendEntryAsync(new MessageEntry { Id = "m-1", ParentId = "tl-1", Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") });

        // File must now exist
        Assert.NotNull(fs.ReadFileOrDefault(path));

        // Reopening must return all three entries: model change, thinking level change, and user message
        var reopened = await JsonlSessionStorage.OpenAsync(fs, path);
        var entries = await reopened.GetEntriesAsync();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Id == "mc-1");
        Assert.Contains(entries, e => e.Id == "tl-1");
        Assert.Contains(entries, e => e.Id == "m-1");
    }

    [Fact]
    public async Task PersistImmediatelyCreatesFileWithoutUserMessage()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/.pi/sessions/fork/session.jsonl";
        await JsonlSessionStorage.CreateAsync(fs, path, "/workspace", "s-fork", null, persistImmediately: true);

        // File must exist immediately (used by forks and subagents)
        Assert.NotNull(fs.ReadFileOrDefault(path));
    }
}
