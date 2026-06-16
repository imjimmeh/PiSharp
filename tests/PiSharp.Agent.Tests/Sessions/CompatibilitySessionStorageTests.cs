using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using Xunit;

namespace PiSharp.Agent.Tests.Sessions;

public sealed class CompatibilitySessionStorageTests
{
    [Fact]
    public async Task SetLeafDoesNotWriteLeafEntryByDefault()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/session.jsonl";
        var storage = await JsonlSessionStorage.CreateAsync(fs, path, "/repo", "sid", null);
        await storage.AppendEntryAsync(new MessageEntry { Id = "m1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") });

        await storage.SetLeafIdAsync("m1");
        var entries = await storage.GetEntriesAsync();

        Assert.Equal("m1", await storage.GetLeafIdAsync());
        Assert.DoesNotContain(entries, entry => entry is LeafEntry);
        Assert.DoesNotContain("\"type\":\"leaf\"", await fs.ReadTextFileAsync(path).ContinueWith(t => t.Result.Value));
    }

    [Fact]
    public async Task OpenAsyncFiltersLegacyLeafEntriesByDefault()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/session.jsonl";
        await fs.WriteFileAsync(path, Header("sid", 3) + Entry(new MessageEntry { Id = "m1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") }) + Entry(new LeafEntry { Id = "leaf1", ParentId = "m1", Timestamp = DateTimeOffset.UtcNow, TargetId = "m1" }));

        var storage = await JsonlSessionStorage.OpenAsync(fs, path);
        var entries = await storage.GetEntriesAsync();

        Assert.Equal("m1", await storage.GetLeafIdAsync());
        Assert.DoesNotContain(entries, entry => entry is LeafEntry);
    }

    [Fact]
    public async Task OpenAsyncToleratesOlderSessionHeaderVersions()
    {
        var fs = new FakeFileSystem();
        var path = "/repo/session.jsonl";
        await fs.WriteFileAsync(path, Header("sid", 2));

        var storage = await JsonlSessionStorage.OpenAsync(fs, path);

        Assert.Equal("sid", (await storage.GetMetadataAsync()).Id);
    }

    [Fact]
    public async Task RepoForkOmitsLeafEntriesInCompatibilityMode()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, "/sessions");
        var source = await repo.CreateAsync(new JsonlSessionCreateOptions("/repo"));
        await source.Storage.AppendEntryAsync(new MessageEntry { Id = "m1", ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User("hello") });
        await source.Storage.AppendEntryAsync(new LeafEntry { Id = "leaf1", ParentId = "m1", Timestamp = DateTimeOffset.UtcNow, TargetId = "m1" });

        var fork = await repo.ForkAsync(source.Metadata, new JsonlSessionCreateOptions("/repo"), new SessionForkOptions(null));

        Assert.DoesNotContain(await fork.Storage.GetEntriesAsync(), entry => entry is LeafEntry);
    }

    private static string Header(string id, int version) => System.Text.Json.JsonSerializer.Serialize(new { type = "session", version, id, timestamp = DateTimeOffset.UtcNow, cwd = "/repo", parentSession = (string?)null }) + "\n";
    private static string Entry(SessionTreeEntry entry) => PiSharp.Agent.Serialization.AgentJsonSerializer.ToJsonLine(entry);
}
