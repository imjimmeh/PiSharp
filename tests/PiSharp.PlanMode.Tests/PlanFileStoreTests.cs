using PiSharp.PlanMode;
using Xunit;

namespace PiSharp.PlanMode.Tests;

public sealed class PlanFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-plan-mode-store", Guid.NewGuid().ToString("N"));
    private readonly PlanFileStore _store;

    public PlanFileStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new PlanFileStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void ShortSessionId_TruncatesToEightCharacters()
    {
        Assert.Equal("01234567", PlanFileStore.ShortSessionId("0123456789abcdef"));
        Assert.Equal("short", PlanFileStore.ShortSessionId("short"));
        Assert.Equal(string.Empty, PlanFileStore.ShortSessionId(null));
        Assert.Equal(string.Empty, PlanFileStore.ShortSessionId(string.Empty));
    }

    [Fact]
    public void BuildPlanPath_UsesShortSessionIdUnderPlansDirectory()
    {
        var path = _store.BuildPlanPath("0123456789abcdef");

        Assert.StartsWith(_root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.EndsWith($"plan-01234567.md", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteDraft_RoundTripsContents()
    {
        var sessionId = "session-abcdefgh";
        var path = _store.BuildPlanPath(sessionId);
        var before = DateTimeOffset.UtcNow;

        await _store.WriteDraftAsync(path, "# The plan\n\n1. Explore\n2. Write", sessionId, "test/planning-model");

        Assert.True(File.Exists(path));
        var contents = await _store.ReadAsync(path);
        Assert.Equal(PlanFileStatus.Draft, contents.Status);
        Assert.Equal(sessionId, contents.SessionId);
        Assert.Equal("test/planning-model", contents.Model);
        // The renderer appends a trailing newline; ReadAsync does not trim it back off.
        Assert.Equal("# The plan\n\n1. Explore\n2. Write", contents.Body.TrimEnd('\r', '\n'));
        Assert.InRange(contents.CreatedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(contents.CreatedAt, contents.UpdatedAt);
    }

    [Fact]
    public async Task WriteDraft_NullModel_RendersEmptyModelField()
    {
        var path = _store.BuildPlanPath("s1");

        await _store.WriteDraftAsync(path, "body", "s1", model: null);

        var contents = await _store.ReadAsync(path);
        Assert.Equal(string.Empty, contents.Model);
    }

    [Fact]
    public async Task WriteDraft_Overwrite_LeavesSingleFileNoTempArtifacts()
    {
        var path = _store.BuildPlanPath("s2");

        await _store.WriteDraftAsync(path, "first body", "s2", null);
        await _store.WriteDraftAsync(path, "second body", "s2", null);

        var contents = await _store.ReadAsync(path);
        Assert.Equal("second body", contents.Body.TrimEnd('\r', '\n'));
        Assert.Single(Directory.GetFiles(_root));
        Assert.All(Directory.GetFiles(_root), file => Assert.EndsWith(".md", file, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetStatus_PreservesBodyAndCreatedAt_UpdatesUpdatedAt()
    {
        var path = _store.BuildPlanPath("s3");
        await _store.WriteDraftAsync(path, "stable body", "s3", null);
        var draft = await _store.ReadAsync(path);
        await Task.Delay(10);

        await _store.SetStatusAsync(path, PlanFileStatus.Approved, "s3", null);

        var approved = await _store.ReadAsync(path);
        Assert.Equal(PlanFileStatus.Approved, approved.Status);
        Assert.Equal("stable body", approved.Body.TrimEnd('\r', '\n'));
        Assert.Equal(draft.CreatedAt, approved.CreatedAt);
        Assert.True(approved.UpdatedAt >= approved.CreatedAt);
    }

    [Fact]
    public async Task SetStatus_Aborted_MarksFileAborted()
    {
        var path = _store.BuildPlanPath("s4");
        await _store.WriteDraftAsync(path, "body", "s4", null);

        await _store.SetStatusAsync(path, PlanFileStatus.Aborted, "s4", null);

        Assert.Equal(PlanFileStatus.Aborted, (await _store.ReadAsync(path)).Status);
    }

    [Fact]
    public async Task ReadAsync_PlainMarkdownWithoutFrontmatter_ReturnsVerbatimBody()
    {
        var path = Path.Combine(_root, "plain.md");
        await File.WriteAllTextAsync(path, "just a plan\n");

        var contents = await _store.ReadAsync(path);

        Assert.Equal(PlanFileStatus.Draft, contents.Status);
        Assert.Equal(string.Empty, contents.SessionId);
        Assert.Null(contents.Model);
        Assert.Equal("just a plan\n", contents.Body);
    }

    [Fact]
    public async Task ReadAsync_UnknownStatus_FallsBackToDraft()
    {
        var path = Path.Combine(_root, "odd.md");
        await File.WriteAllTextAsync(path, "---\nstatus: pending\nsessionId: s5\n---\n\nbody");

        var contents = await _store.ReadAsync(path);

        Assert.Equal(PlanFileStatus.Draft, contents.Status);
        Assert.Equal("s5", contents.SessionId);
        Assert.Equal("body", contents.Body);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_Throws()
    {
        var path = Path.Combine(_root, "missing.md");

        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.ReadAsync(path));
    }

    [Fact]
    public async Task StatusFlips_LeaveNoTempFilesBehind()
    {
        var path = _store.BuildPlanPath("s6");
        await _store.WriteDraftAsync(path, "body", "s6", null);
        await _store.SetStatusAsync(path, PlanFileStatus.Approved, "s6", null);
        await _store.SetStatusAsync(path, PlanFileStatus.Aborted, "s6", null);

        Assert.All(Directory.GetFiles(_root), file => Assert.EndsWith(".md", file, StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.GetFiles(_root), file => file.Contains(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FrontmatterDates_RoundTripThroughIsoFormat()
    {
        var path = _store.BuildPlanPath("s7");
        await _store.WriteDraftAsync(path, "body", "s7", "model-x");

        var contents = await _store.ReadAsync(path);
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains(contents.CreatedAt.ToString("O"), text, StringComparison.Ordinal);
        Assert.Contains(contents.UpdatedAt.ToString("O"), text, StringComparison.Ordinal);
    }
}
