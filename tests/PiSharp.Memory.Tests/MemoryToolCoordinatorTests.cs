using System.Text.Json;
using PiSharp.Memory.Abstractions;
using PiSharp.Memory.Backends.File;
using PiSharp.Memory.Backends.Off;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class MemoryToolCoordinatorTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static MemoryToolCoordinator CreateCoordinator(IMemoryProvider provider, out IMemoryStore store)
    {
        store = new MemoryStore(provider, MemoryProjectKeys.Encode(@"C:\proj\one"));
        return new MemoryToolCoordinator(store);
    }

    private static string ContentOf(PiSharp.Agent.Core.Tools.AgentToolResult<object?> result)
        => string.Join("\n", result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Select(c => c.Text));

    // --- blocked path (backend off) ---

    [Theory]
    [InlineData("retain")]
    [InlineData("recall")]
    [InlineData("reflect")]
    [InlineData("memory_edit")]
    [InlineData("learn")]
    public async Task AllTools_BlockedWhenBackendOff(string toolName)
    {
        var coordinator = CreateCoordinator(new OffMemoryProvider(), out _);

        var result = await coordinator.ExecuteAsync(toolName, Args(new { }), CancellationToken.None);

        Assert.Equal(MemoryToolCoordinator.BlockedMessage, ContentOf(result));
        var details = Assert.IsType<MemoryToolDetails>(result.Details);
        Assert.True(details.Blocked);
        Assert.Equal(toolName, details.Tool);
    }

    // --- retain ---

    [Fact]
    public async Task Retain_StoresRecordAndReturnsDefaultSlugKey()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);

        var result = await coordinator.ExecuteAsync("retain", Args(new
        {
            title = "OAuth setup",
            content = "Tokens live in auth.json",
            tags = new[] { "oauth" }
        }), CancellationToken.None);

        var details = Assert.IsType<MemoryToolDetails>(result.Details);
        Assert.False(details.Blocked);
        Assert.StartsWith("facts/", details.RecordKey);
        Assert.Contains("oauth", details.RecordKey);

        var records = await store.ListAsync(MemoryScope.Project, new MemoryQuery());
        var record = Assert.Single(records);
        Assert.Equal(details.RecordKey, record.RecordKey);
        Assert.Equal(MemoryKind.Fact, record.Kind);
    }

    [Fact]
    public async Task Retain_CallerKey_IsIdempotent()
    {
        var root = MemoryTestHelpers.TempDir();
        var coordinator = CreateCoordinator(new FileMemoryProvider(root, MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);

        var first = await coordinator.ExecuteAsync("retain", Args(new { title = "A", content = "one", recordKey = "facts/oauth-setup" }), CancellationToken.None);
        var second = await coordinator.ExecuteAsync("retain", Args(new { title = "A", content = "two", recordKey = "facts/oauth-setup" }), CancellationToken.None);

        Assert.Equal("facts/oauth-setup", Assert.IsType<MemoryToolDetails>(first.Details).RecordKey);
        Assert.Equal("facts/oauth-setup", Assert.IsType<MemoryToolDetails>(second.Details).RecordKey);
        Assert.Single(await store.ListAsync(MemoryScope.Project, new MemoryQuery()));
        Assert.Equal("two", (await store.GetAsync(MemoryScope.Project, "facts/oauth-setup"))!.Content);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("global")]
    public async Task Retain_InvalidScope_ReturnsError(string scope)
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("retain", Args(new { scope, title = "A", content = "B" }), CancellationToken.None);

        var details = Assert.IsType<MemoryToolDetails>(result.Details);
        Assert.True(details.Error);
        Assert.Contains("Invalid scope", details.ErrorMessage);
    }

    [Fact]
    public async Task Retain_InvalidKind_ReturnsError()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("retain", Args(new { kind = "anecdote", title = "A", content = "B" }), CancellationToken.None);

        Assert.True(Assert.IsType<MemoryToolDetails>(result.Details).Error);
    }

    [Fact]
    public async Task Retain_MissingTitle_ReturnsError()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("retain", Args(new { content = "B" }), CancellationToken.None);

        Assert.True(Assert.IsType<MemoryToolDetails>(result.Details).Error);
    }

    // --- recall ---

    [Fact]
    public async Task Recall_ReturnsStoredRecordsAsMarkdown()
    {
        var root = MemoryTestHelpers.TempDir();
        var coordinator = CreateCoordinator(new FileMemoryProvider(root, MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);
        await store.PutAsync(MemoryScope.Project, new MemoryRecord("facts/oauth-setup", MemoryKind.Fact, "OAuth", "device flow", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var result = await coordinator.ExecuteAsync("recall", Args(new { query = "oauth" }), CancellationToken.None);

        var content = ContentOf(result);
        Assert.Contains("facts/oauth-setup", content);
        Assert.Equal(1, Assert.IsType<MemoryToolDetails>(result.Details).Count);
    }

    [Fact]
    public async Task Recall_NoMatches_ReportsEmpty()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("recall", Args(new { query = "nothing" }), CancellationToken.None);

        Assert.Contains("No matching memory records", ContentOf(result));
        Assert.Equal(0, Assert.IsType<MemoryToolDetails>(result.Details).Count);
    }

    // --- learn ---

    [Fact]
    public async Task Learn_StoresLesson()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);

        var result = await coordinator.ExecuteAsync("learn", Args(new { title = "Build first", lesson = "Run dotnet build before commit" }), CancellationToken.None);

        var details = Assert.IsType<MemoryToolDetails>(result.Details);
        Assert.StartsWith("lessons/", details.RecordKey);
        var records = await store.ListAsync(MemoryScope.Project, new MemoryQuery(Kind: MemoryKind.Lesson));
        var lesson = Assert.Single(records);
        Assert.Equal("Run dotnet build before commit", lesson.Content);
    }

    [Fact]
    public async Task Learn_PromoteWithoutSkillName_Warns()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("learn", Args(new { title = "T", lesson = "L", promote = true }), CancellationToken.None);

        var details = Assert.IsType<MemoryToolDetails>(result.Details);
        Assert.NotNull(details.Warning);
        Assert.Contains("skillName", ContentOf(result));
    }

    [Fact]
    public async Task Learn_PromoteWithoutManagedSkillStore_Warns()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("learn", Args(new { title = "T", lesson = "L", promote = true, skillName = "build-first" }), CancellationToken.None);

        var details = Assert.IsType<MemoryToolDetails>(result.Details);
        Assert.NotNull(details.Warning);
        Assert.Contains("P04", ContentOf(result));
    }

    [Fact]
    public async Task Learn_PromoteWithPromoter_CallsPromoter()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);
        var promoted = new List<string>();
        coordinator.SkillPromoter = (name, description, ct) =>
        {
            promoted.Add(name);
            return Task.FromResult<string?>(name);
        };

        var result = await coordinator.ExecuteAsync("learn", Args(new { title = "T", lesson = "L", promote = true, skillName = "build-first" }), CancellationToken.None);

        Assert.Equal(["build-first"], promoted);
        Assert.Contains("Promoted to managed skill 'build-first'", ContentOf(result));
    }

    // --- memory_edit ---

    [Fact]
    public async Task MemoryEdit_UpdatesPartialFields()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);
        await store.PutAsync(MemoryScope.Project, new MemoryRecord("facts/oauth-setup", MemoryKind.Fact, "OAuth", "old content", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var result = await coordinator.ExecuteAsync("memory_edit", Args(new { recordKey = "facts/oauth-setup", content = "new content" }), CancellationToken.None);

        Assert.Contains("Updated", ContentOf(result));
        var record = await store.GetAsync(MemoryScope.Project, "facts/oauth-setup");
        Assert.Equal("new content", record!.Content);
        Assert.Equal("OAuth", record.Title); // untouched field preserved
    }

    [Fact]
    public async Task MemoryEdit_Invalidate_HidesRecord()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);
        await store.PutAsync(MemoryScope.Project, new MemoryRecord("facts/oauth-setup", MemoryKind.Fact, "OAuth", "secret", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var result = await coordinator.ExecuteAsync("memory_edit", Args(new { recordKey = "facts/oauth-setup", invalidate = true }), CancellationToken.None);

        Assert.Contains("Invalidated", ContentOf(result));
        Assert.Empty(await store.ListAsync(MemoryScope.Project, new MemoryQuery()));
        Assert.True((await store.ListAsync(MemoryScope.Project, new MemoryQuery(IncludeInvalidated: true)))[0].IsInvalidated);
    }

    [Fact]
    public async Task MemoryEdit_MissingKey_ReturnsError()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);

        var result = await coordinator.ExecuteAsync("memory_edit", Args(new { title = "x" }), CancellationToken.None);

        Assert.True(Assert.IsType<MemoryToolDetails>(result.Details).Error);
    }

    // --- reflect ---

    [Fact]
    public async Task Reflect_IsReadOnlyAndRendersMaterial()
    {
        var root = MemoryTestHelpers.TempDir();
        var coordinator = CreateCoordinator(new FileMemoryProvider(root, MemoryProjectKeys.Encode(@"C:\proj\one")), out var store);
        await store.PutAsync(MemoryScope.Project, new MemoryRecord("facts/oauth-setup", MemoryKind.Fact, "OAuth", "device flow", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var result = await coordinator.ExecuteAsync("reflect", Args(new { topic = "oauth" }), CancellationToken.None);

        var content = ContentOf(result);
        Assert.Contains("Memory reflection", content);
        Assert.Contains("facts/oauth-setup", content);
        // Read-only: nothing new was stored.
        Assert.Single(await store.ListAsync(MemoryScope.Project, new MemoryQuery()));
    }

    [Fact]
    public async Task UnknownTool_ReturnsError()
    {
        var coordinator = CreateCoordinator(new FileMemoryProvider(MemoryTestHelpers.TempDir(), MemoryProjectKeys.Encode(@"C:\proj\one")), out _);
        var result = await coordinator.ExecuteAsync("nope", Args(new { }), CancellationToken.None);
        Assert.True(Assert.IsType<MemoryToolDetails>(result.Details).Error);
    }
}
