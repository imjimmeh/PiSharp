using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using Xunit;

namespace PiSharp.Agent.Tests.Sessions;

public sealed class JsonlSessionRepoTests
{
    [Fact]
    public void EncodesCwdLikeTypescriptRepo()
    {
        Assert.Equal("--tmp-my-project--", SessionRepoUtils.EncodeCwd("/tmp/my-project"));
    }

    [Fact]
    public async Task CreateSessionReturnsUsableSession()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var session = await repo.CreateAsync(new JsonlSessionCreateOptions("/workspace"));

        Assert.NotEmpty(session.Id);
        var entries = await session.GetEntriesAsync();
        Assert.Empty(entries);
        Assert.Equal("/workspace", session.Metadata.Cwd);
    }

    [Fact]
    public async Task CreateSessionDefersFileUntilFirstMessage()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");

        var session = await repo.CreateAsync(new JsonlSessionCreateOptions("/workspace"));

        Assert.False((await fs.ExistsAsync(session.Metadata.Path)).Value);
        Assert.Empty(await repo.ListAsync(new JsonlSessionListOptions("/workspace")));

        await session.AppendMessageAsync(AgentMessages.User("hello"));

        Assert.True((await fs.ExistsAsync(session.Metadata.Path)).Value);
        var persisted = Assert.Single(await repo.ListAsync(new JsonlSessionListOptions("/workspace")));
        Assert.Equal(session.Id, persisted.Id);
    }

    [Fact]
    public async Task CreateAndListOrdersByLatestMessageActivity()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var newerCreated = await repo.CreateAsync(new JsonlSessionCreateOptions("/a"));
        var olderCreated = await repo.CreateAsync(new JsonlSessionCreateOptions("/a"));
        await newerCreated.AppendMessageAsync(AgentMessages.User("old activity", DateTimeOffset.Parse("2026-05-01T10:00:00Z")));
        await olderCreated.AppendMessageAsync(AgentMessages.User("new activity", DateTimeOffset.Parse("2026-05-01T11:00:00Z")));

        var list = await repo.ListAsync(new JsonlSessionListOptions("/a"));
        Assert.Equal(2, list.Count);
        Assert.Equal(olderCreated.Id, list[0].Id);
        Assert.Equal(newerCreated.Id, list[1].Id);
    }

    [Fact]
    public async Task CreateAndListScopedToCwd()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var a = await repo.CreateAsync(new JsonlSessionCreateOptions("/a"));
        var b = await repo.CreateAsync(new JsonlSessionCreateOptions("/b"));
        await a.AppendMessageAsync(AgentMessages.User("a"));
        await b.AppendMessageAsync(AgentMessages.User("b"));

        var listA = await repo.ListAsync(new JsonlSessionListOptions("/a"));
        var listAll = await repo.ListAsync();

        Assert.Single(listA);
        Assert.Equal(a.Id, listA[0].Id);
        Assert.Equal(2, listAll.Count);
    }

    [Fact]
    public async Task ListLoadsSessionMetadataWithBoundedConcurrency()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        for (var i = 0; i < 20; i++)
        {
            var session = await repo.CreateAsync(new JsonlSessionCreateOptions("/a"));
            await session.AppendMessageAsync(AgentMessages.User($"message {i}"));
        }
        var inFlight = 0;
        var maxInFlight = 0;
        fs.BeforeReadTextFileAsync = async (path, _) =>
        {
            if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
            var current = Interlocked.Increment(ref inFlight);
            maxInFlight = Math.Max(maxInFlight, current);
            await Task.Delay(20);
            Interlocked.Decrement(ref inFlight);
        };

        var list = await repo.ListAsync(new JsonlSessionListOptions("/a"));

        Assert.Equal(20, list.Count);
        Assert.InRange(maxInFlight, 2, 10);
    }

    [Fact]
    public async Task ForkBeforeCopiesAllEntries()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var source = await repo.CreateAsync(new JsonlSessionCreateOptions("/src"));

        var m1 = await source.AppendMessageAsync(AgentMessages.User("one"));
        var m2 = await source.AppendMessageAsync(AgentMessages.User("two"));
        var fork = await repo.ForkAsync(source.Metadata, new JsonlSessionCreateOptions("/src"), new SessionForkOptions());

        var forkEntries = await fork.GetEntriesAsync();
        Assert.Equal(2, forkEntries.Count);
        Assert.Equal(source.Metadata.Path, fork.Metadata.ParentSessionPath);
    }

    [Fact]
    public async Task ForkEmptySessionCreatesFileImmediately()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var source = await repo.CreateAsync(new JsonlSessionCreateOptions("/src", PersistImmediately: true));

        var fork = await repo.ForkAsync(source.Metadata, new JsonlSessionCreateOptions("/src", "forked"), new SessionForkOptions());

        Assert.True((await fs.ExistsAsync(fork.Metadata.Path)).Value);
        Assert.Equal("forked", fork.Id);
        Assert.Empty(await fork.GetEntriesAsync());
    }

    [Fact]
    public async Task ForkBeforeEntryCopiesPrefixUpToMessage()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var source = await repo.CreateAsync(new JsonlSessionCreateOptions("/src"));

        var m1 = await source.AppendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.User("one"));
        var _ = await source.AppendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.Assistant("two"));
        var m3 = await source.AppendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.User("three"));

        var fork = await repo.ForkAsync(source.Metadata, new JsonlSessionCreateOptions("/src"), new SessionForkOptions(m3));

        var entries = await fork.GetEntriesAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == m1);
        Assert.DoesNotContain(entries, e => e.Id == m3);
    }

    [Fact]
    public async Task ForkAtEntryCopiesPathToRootIncludingEntry()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var source = await repo.CreateAsync(new JsonlSessionCreateOptions("/src"));

        var m1 = await source.AppendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.User("one"));
        var m2 = await source.AppendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.Assistant("two"));

        var fork = await repo.ForkAsync(source.Metadata, new JsonlSessionCreateOptions("/src"), new SessionForkOptions(m2, "at"));

        var entries = await fork.GetEntriesAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == m1);
        Assert.Contains(entries, e => e.Id == m2);
    }

    [Fact]
    public async Task DeleteRemovesFile()
    {
        var fs = new FakeFileSystem();
        var repo = new JsonlSessionRepo(fs, ".pi/sessions");
        var source = await repo.CreateAsync(new JsonlSessionCreateOptions("/src"));

        await repo.DeleteAsync(source.Metadata);
        Assert.False((await fs.ExistsAsync(source.Metadata.Path)).Value);
    }
}
