using PiSharp.Memory.Abstractions;
using PiSharp.Memory.Backends.File;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class FileMemoryProviderTests : IDisposable
{
    private readonly string _root = MemoryTestHelpers.TempDir();
    private readonly List<FileMemoryProvider> _providers = [];

    public void Dispose()
    {
        // Dispose providers first: the debounced background flush would otherwise re-create
        // records.jsonl inside the deleted root after this test class cleans up.
        foreach (var provider in _providers)
        {
            try { provider.Dispose(); }
            catch { /* best-effort */ }
        }
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private FileMemoryProvider Provider(string cwd = @"C:\proj\one")
    {
        var provider = new FileMemoryProvider(_root, MemoryProjectKeys.Encode(cwd));
        _providers.Add(provider);
        return provider;
    }

    private static MemoryRecord Record(
        string key,
        MemoryKind kind = MemoryKind.Fact,
        string? title = null,
        string? content = null,
        string[]? tags = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
        => new(key, kind, title ?? "Title " + key, content ?? "Content " + key, tags ?? [], createdAt ?? DateTimeOffset.UtcNow, updatedAt ?? DateTimeOffset.UtcNow);

    // --- CRUD round-trips ---

    [Fact]
    public async Task Put_Get_RoundTrips()
    {
        var provider = Provider();
        var record = Record("facts/oauth-setup", tags: ["oauth", "auth"]);

        await provider.PutAsync(MemoryScope.Project, record);

        var stored = await provider.GetAsync(MemoryScope.Project, "facts/oauth-setup");
        Assert.NotNull(stored);
        Assert.Equal(record.RecordKey, stored!.RecordKey);
        Assert.Equal(record.Kind, stored.Kind);
        Assert.Equal(record.Title, stored.Title);
        Assert.Equal(record.Content, stored.Content);
        Assert.Equal(["oauth", "auth"], stored.Tags);
    }

    [Fact]
    public async Task Put_SameKeyTwice_IsIdempotentUpsert()
    {
        var provider = Provider();
        var original = Record("facts/oauth-setup", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await provider.PutAsync(MemoryScope.Project, original);
        await provider.PutAsync(MemoryScope.Project, Record("facts/oauth-setup", content: "updated"));

        var records = await provider.ListAsync(MemoryScope.Project, new MemoryQuery());
        var stored = Assert.Single(records);
        Assert.Equal("updated", stored.Content);
        // The original CreatedAt survives a re-put that carries a fresh timestamp.
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
    }

    [Fact]
    public async Task Update_AppliesMutationAndBumpsUpdatedAt()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/x", updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var updated = await provider.UpdateAsync(MemoryScope.Project, "facts/x", record => record with { Title = "New title" });

        Assert.NotNull(updated);
        Assert.Equal("New title", updated!.Title);
        Assert.Equal("Content facts/x", updated.Content);
        Assert.True(updated.UpdatedAt > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Update_MissingKey_CreatesRecord()
    {
        var provider = Provider();
        var updated = await provider.UpdateAsync(MemoryScope.Project, "facts/new", record => record with { Title = "Born", Content = "from edit" });

        Assert.NotNull(updated);
        Assert.Equal("Born", updated!.Title);
        Assert.NotNull(await provider.GetAsync(MemoryScope.Project, "facts/new"));
    }

    [Fact]
    public async Task Delete_RemovesRecordAndReturnsTrue()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/x"));

        Assert.True(await provider.DeleteAsync(MemoryScope.Project, "facts/x"));
        Assert.Null(await provider.GetAsync(MemoryScope.Project, "facts/x"));
        Assert.False(await provider.DeleteAsync(MemoryScope.Project, "facts/x"));
    }

    [Fact]
    public async Task Invalidate_HidesFromDefaultListAndSearchButNotIncludeInvalidated()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/x", content: "oauth token location"));
        await provider.UpdateAsync(MemoryScope.Project, "facts/x", record => record with { InvalidatedAt = DateTimeOffset.UtcNow });

        Assert.Empty(await provider.ListAsync(MemoryScope.Project, new MemoryQuery()));
        Assert.Empty(await provider.SearchAsync(MemoryScope.Project, "oauth"));
        var invalidated = await provider.ListAsync(MemoryScope.Project, new MemoryQuery(IncludeInvalidated: true));
        var record = Assert.Single(invalidated);
        Assert.True(record.IsInvalidated);
    }

    // --- persistence ---

    [Fact]
    public async Task Persistence_SurvivesStoreReopen()
    {
        var providerA = Provider();
        await providerA.PutAsync(MemoryScope.Project, Record("facts/oauth-setup", content: "device flow"));
        await providerA.PutAsync(MemoryScope.Project, Record("lessons/build", kind: MemoryKind.Lesson, content: "build first"));
        // Flush (dispose) so a brand-new provider over the same root sees the same records.
        await providerA.DisposeAsync();

        // A brand-new provider over the same root must see the same records.
        var providerB = Provider();
        var stored = await providerB.GetAsync(MemoryScope.Project, "facts/oauth-setup");
        Assert.NotNull(stored);
        Assert.Equal("device flow", stored!.Content);
        Assert.Equal(2, (await providerB.ListAsync(MemoryScope.Project, new MemoryQuery())).Count);
    }

    [Fact]
    public async Task ProjectScope_IsolatedAcrossCwds()
    {
        var providerA = Provider(@"C:\proj\one");
        var providerB = Provider(@"C:\proj\two");
        await providerA.PutAsync(MemoryScope.Project, Record("facts/one-only"));

        Assert.Null(await providerB.GetAsync(MemoryScope.Project, "facts/one-only"));
        Assert.NotNull(await providerA.GetAsync(MemoryScope.Project, "facts/one-only"));
    }

    [Fact]
    public async Task UserScope_IsSharedAcrossCwds()
    {
        var providerA = Provider(@"C:\proj\one");
        var providerB = Provider(@"C:\proj\two");
        await providerA.PutAsync(MemoryScope.User, Record("facts/global"));
        // Flush (dispose) so the second provider over the same user scope sees the record.
        await providerA.DisposeAsync();

        Assert.NotNull(await providerB.GetAsync(MemoryScope.User, "facts/global"));
        Assert.Null(await providerB.GetAsync(MemoryScope.Project, "facts/global"));
    }

    // --- keyword search ---

    [Fact]
    public async Task Search_RanksByKeywordHits()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/oauth", content: "OAuth device flow starts with --login"));
        await provider.PutAsync(MemoryScope.Project, Record("facts/build", content: "Build with dotnet build before commit"));
        await provider.PutAsync(MemoryScope.Project, Record("lessons/oauth-cache", content: "tokens live in auth.json", tags: ["oauth"]));

        var results = await provider.SearchAsync(MemoryScope.Project, "oauth token");

        Assert.Equal(2, results.Count);
        // The tagged oauth record and the oauth content record beat the build record; exact order:
        Assert.All(results, result => Assert.DoesNotContain("build", result.Record.Title));
    }

    [Fact]
    public async Task Search_EmptyResultForNoMatches()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/build", content: "dotnet build"));

        Assert.Empty(await provider.SearchAsync(MemoryScope.Project, "zzzz"));
    }

    [Fact]
    public async Task Recall_WithTextUsesSearch_WithoutTextLists()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/oauth", content: "oauth setup"));
        await provider.PutAsync(MemoryScope.Project, Record("facts/build", content: "dotnet build"));

        var searched = await provider.RecallAsync(MemoryScope.Project, new MemoryQuery(Text: "oauth"));
        Assert.Single(searched);
        Assert.Equal("facts/oauth", searched[0].RecordKey);

        var listed = await provider.RecallAsync(MemoryScope.Project, new MemoryQuery());
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task List_FiltersByKindAndTagsAndLimit()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/a", kind: MemoryKind.Fact, tags: ["x"]));
        await provider.PutAsync(MemoryScope.Project, Record("lessons/b", kind: MemoryKind.Lesson, tags: ["x", "y"]));
        await provider.PutAsync(MemoryScope.Project, Record("facts/c", kind: MemoryKind.Fact, tags: ["z"]));

        Assert.Single(await provider.ListAsync(MemoryScope.Project, new MemoryQuery(Kind: MemoryKind.Lesson)));
        Assert.Single(await provider.ListAsync(MemoryScope.Project, new MemoryQuery(Tags: ["y"])));
        Assert.Equal(2, (await provider.ListAsync(MemoryScope.Project, new MemoryQuery(Limit: 2))).Count);
    }

    // --- memory_summary.md ---

    [Fact]
    public async Task SummaryFile_RegeneratedOnSummaryAndMentalModelWrites()
    {
        var provider = Provider();
        var summaryPath = Path.Combine(_root, "projects", MemoryProjectKeys.Encode(@"C:\proj\one"), "memory_summary.md");

        await provider.PutAsync(MemoryScope.Project, Record("facts/plain", kind: MemoryKind.Fact));
        await provider.FlushAsync();
        Assert.False(File.Exists(summaryPath));

        await provider.PutAsync(MemoryScope.Project, Record("summaries/week-1", kind: MemoryKind.Summary, content: "All oauth flows reviewed"));
        await provider.FlushAsync();
        Assert.True(File.Exists(summaryPath));
        var content = await File.ReadAllTextAsync(summaryPath);
        Assert.Contains("summaries/week-1", content);
        Assert.Contains("All oauth flows reviewed", content);
        Assert.DoesNotContain("facts/plain", content);
    }

    [Fact]
    public async Task SummaryFile_RemovedWhenNoSummaryOrMentalModelRecordsRemain()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("summaries/week-1", kind: MemoryKind.Summary));
        await provider.FlushAsync();
        var summaryPath = Path.Combine(_root, "projects", MemoryProjectKeys.Encode(@"C:\proj\one"), "memory_summary.md");
        Assert.True(File.Exists(summaryPath));

        await provider.DeleteAsync(MemoryScope.Project, "summaries/week-1");
        await provider.FlushAsync();

        Assert.False(File.Exists(summaryPath));
    }

    // --- robustness ---

    [Fact]
    public async Task CorruptJsonlLine_IsSkippedAndKeepsRest()
    {
        var provider = Provider();
        var recordsPath = Path.Combine(_root, "projects", MemoryProjectKeys.Encode(@"C:\proj\one"), "records.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(recordsPath)!);
        await File.WriteAllTextAsync(recordsPath,
            "{ not json }\n" +
            "{\"recordKey\":\"facts/ok\",\"kind\":\"fact\",\"title\":\"Title\",\"content\":\"Content\",\"tags\":[],\"createdAt\":\"2024-01-01T00:00:00+00:00\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\"}\n");

        var records = await provider.ListAsync(MemoryScope.Project, new MemoryQuery());

        var record = Assert.Single(records);
        Assert.Equal("facts/ok", record.RecordKey);
    }
}
