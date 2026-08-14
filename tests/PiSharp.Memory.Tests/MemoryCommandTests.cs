using PiSharp.Abstractions.Messages;
using PiSharp.Memory.Abstractions;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class MemoryCommandTests
{
    private static async Task<MemoryHarness> CreateHarnessAsync(
        IReadOnlyDictionary<string, object?>? settings = null,
        string cwd = @"C:\proj\one")
        => await MemoryHarness.CreateAsync(cwd, settings ?? new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });

    private static string LastReply(MemoryHarness harness)
        => string.Join("\n", harness.SentMessages[^1].Message switch
        {
            UserMessage user => user.Content.OfType<TextContent>().Select(c => c.Text),
            _ => []
        });

    [Fact]
    public async Task Summary_ListsBackendProjectKeyAndCounts()
    {
        var harness = await CreateHarnessAsync();
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord("facts/a", MemoryKind.Fact, "A", "a", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await harness.Extension.Store!.PutAsync(MemoryScope.User, new MemoryRecord("facts/b", MemoryKind.Fact, "B", "b", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var command = harness.FindCommand("memory")!;
        await command.Handler(string.Empty, CancellationToken.None);

        var reply = LastReply(harness);
        Assert.Contains("Backend: file", reply);
        Assert.Contains(MemoryProjectKeys.Encode(@"C:\proj\one"), reply);
        Assert.Contains("1 project / 1 user", reply);
    }

    [Fact]
    public async Task List_ShowsRecordsAndFiltersByKind()
    {
        var harness = await CreateHarnessAsync();
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord("facts/a", MemoryKind.Fact, "A", "a", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord("lessons/b", MemoryKind.Lesson, "B", "b", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var command = harness.FindCommand("memory")!;
        await command.Handler("list", CancellationToken.None);
        var all = LastReply(harness);
        Assert.Contains("facts/a", all);
        Assert.Contains("lessons/b", all);

        await command.Handler("list lesson", CancellationToken.None);
        var lessons = LastReply(harness);
        Assert.Contains("lessons/b", lessons);
        Assert.DoesNotContain("facts/a", lessons);

        await command.Handler("list anecdote", CancellationToken.None);
        Assert.Contains("Invalid kind", LastReply(harness));
    }

    [Fact]
    public async Task Show_ReturnsRecordContent()
    {
        var harness = await CreateHarnessAsync();
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord("facts/oauth-setup", MemoryKind.Fact, "OAuth", "tokens live in auth.json", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var command = harness.FindCommand("memory")!;
        await command.Handler("show facts/oauth-setup", CancellationToken.None);

        var reply = LastReply(harness);
        Assert.Contains("tokens live in auth.json", reply);
    }

    [Fact]
    public async Task Show_MissingKey_ReportsAbsent()
    {
        var harness = await CreateHarnessAsync();
        var command = harness.FindCommand("memory")!;

        await command.Handler("show facts/nope", CancellationToken.None);

        Assert.Contains("No record 'facts/nope'", LastReply(harness));
    }

    [Fact]
    public async Task Forget_HardDeletesRecord()
    {
        var harness = await CreateHarnessAsync();
        await harness.Extension.Store!.PutAsync(MemoryScope.Project, new MemoryRecord("facts/a", MemoryKind.Fact, "A", "a", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var command = harness.FindCommand("memory")!;
        await command.Handler("forget facts/a", CancellationToken.None);

        Assert.Contains("Forgot 'facts/a'", LastReply(harness));
        Assert.Null(await harness.Extension.Store!.GetAsync(MemoryScope.Project, "facts/a"));
    }

    [Fact]
    public async Task Backend_ShowsConfiguredAndActiveBackend()
    {
        var harness = await CreateHarnessAsync();
        var command = harness.FindCommand("memory")!;

        await command.Handler("backend", CancellationToken.None);

        Assert.Contains("extensions.pisharp-memory.backend = \"file\"", LastReply(harness));
    }

    [Fact]
    public async Task UnknownCommand_ShowsUsage()
    {
        var harness = await CreateHarnessAsync();
        var command = harness.FindCommand("memory")!;

        await command.Handler("frobnicate", CancellationToken.None);

        Assert.Contains("Usage: /memory", LastReply(harness));
    }

    [Fact]
    public async Task Command_IsNotRegisteredWhenDisabled()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?> { ["enabled"] = false });

        Assert.Null(harness.FindCommand("memory"));
    }
}
