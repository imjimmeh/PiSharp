using Xunit;

namespace PiSharp.Git.Tests;

public sealed class CommitInventoryServiceTests : IAsyncLifetime
{
    private GitFixture? _fixture;
    private CommitInventoryService _service = null!;

    public async Task InitializeAsync()
    {
        _fixture = new GitFixture();
        var runner = new GitRunner();
        _service = new CommitInventoryService(runner, new ChangeClassifier(new GitPluginOptions()));
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    private async Task<CommitInventoryService.CaptureResult> CaptureAsync()
        => await _service.CaptureAsync(_fixture!.RepoPath);

    [Fact]
    public async Task UntrackedFileIsClassified()
    {
        _fixture!.WriteFile("src/app.cs", "code\n");

        var capture = await CaptureAsync();

        Assert.True(capture.Success);
        Assert.Equal("main", capture.Inventory!.Branch);
        Assert.Equal(_fixture.Head(), capture.Inventory.HeadHash);
        var item = Assert.Single(capture.Inventory.Changes);
        Assert.Equal("src/app.cs", item.Path);
        Assert.True(item.IsUntracked);
        Assert.Equal(ChangeCategory.Source, item.Category);
    }

    [Fact]
    public async Task StagedAndWorktreeChangesBothCaptured()
    {
        _fixture!.WriteFile("staged.cs", "1\n");
        _fixture.Add("staged.cs");
        _fixture.WriteFile("worktree.cs", "2\n");

        var capture = await CaptureAsync();

        Assert.True(capture.Success);
        var staged = capture.Inventory!.Changes.Single(c => c.Path == "staged.cs");
        var worktree = capture.Inventory.Changes.Single(c => c.Path == "worktree.cs");
        Assert.True(staged.IsStaged);
        Assert.False(staged.IsUnstaged);
        Assert.True(worktree.IsUnstaged);
        Assert.False(worktree.IsStaged);
    }

    [Fact]
    public async Task RenameIsCapturedWithSource()
    {
        _fixture!.WriteFile("old.txt", "content\n");
        _fixture.Add("old.txt");
        _fixture.Commit("add old");
        _fixture.MoveFile("old.txt", "new.txt");
        _fixture.Run("add", "-A");

        var capture = await CaptureAsync();

        Assert.True(capture.Success);
        var rename = Assert.Single(capture.Inventory!.Changes);
        Assert.True(rename.IsRename);
        Assert.Equal("new.txt", rename.Path);
        Assert.Equal("old.txt", rename.RenameSource);
    }

    [Fact]
    public async Task UnmergedConflictsRejectCapture()
    {
        _fixture!.WriteFile("a.txt", "base\n");
        _fixture.Add("a.txt");
        _fixture.Commit("base");

        _fixture.Run("checkout", "-q", "-b", "other");
        _fixture.WriteFile("a.txt", "other\n");
        _fixture.Add("a.txt");
        _fixture.Commit("other change");

        _fixture.Run("checkout", "-q", "main");
        _fixture.WriteFile("a.txt", "main\n");
        _fixture.Add("a.txt");
        _fixture.Commit("main change");

        try { _fixture.Run("merge", "other"); } catch (InvalidOperationException) { /* conflict expected */ }

        var capture = await CaptureAsync();

        Assert.False(capture.Success);
        Assert.Contains("Unresolved merge conflicts", capture.Error);
    }

    [Fact]
    public async Task NonRepositoryIsRejected()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pisharp-norepo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var capture = await _service.CaptureAsync(temp);
            Assert.False(capture.Success);
            Assert.Contains("Not a git repository", capture.Error);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScopeFilesRestrictsCapture()
    {
        _fixture!.WriteFile("src/keep.cs", "k\n");
        _fixture!.WriteFile("src/skip.cs", "s\n");

        var capture = await _service.CaptureAsync(_fixture!.RepoPath,
            new CommitInventoryService.CaptureOptions(ScopeFiles: ["src/keep.cs"]));

        Assert.True(capture.Success);
        Assert.Contains("src/keep.cs", capture.Inventory!.Changes.Select(c => c.Path));
        Assert.DoesNotContain("src/skip.cs", capture.Inventory.Changes.Select(c => c.Path));
    }

    [Fact]
    public async Task ExcludedFilesReportedSeparately()
    {
        _fixture!.WriteFile("package-lock.json", "{}\n");
        _fixture!.WriteFile("app.cs", "x\n");

        var capture = await CaptureAsync();

        Assert.True(capture.Success);
        Assert.Contains("package-lock.json", capture.Inventory!.ExcludedFiles);
        Assert.DoesNotContain(capture.Inventory.Changes, c => c.Path == "package-lock.json");
        Assert.Contains("app.cs", capture.Inventory.Changes.Select(c => c.Path));
    }
}
