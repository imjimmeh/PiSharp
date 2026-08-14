using Xunit;

namespace PiSharp.Git.Tests;

public sealed class CommitExecutorTests : IAsyncLifetime
{
    private GitFixture? _fixture;
    private GitRunner _runner = null!;
    private CommitInventoryService _inventory = null!;
    private CommitExecutor _executor = null!;

    private async Task<CommitInventoryService.CaptureResult> CaptureAsync()
        => await _inventory.CaptureAsync(_fixture!.RepoPath);

    private async Task<CommitExecutor.ExecuteOutcome> ExecuteAsync(
        ChangeInventory? inventory,
        params CommitGroupInput[] groups) => await _executor.ExecuteAsync(new CommitExecutor.ExecuteRequest(
            _fixture!.RepoPath, inventory!, groups, RunHooks: true, DryRun: false));

    public async Task InitializeAsync()
    {
        _fixture = new GitFixture();
        _runner = new GitRunner();
        _inventory = new CommitInventoryService(_runner, new ChangeClassifier(new GitPluginOptions()));
        _executor = new CommitExecutor(_runner, new CommitGraph());
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    private static CommitGroupInput Group(string message, params string[] files)
        => new() { Message = message, Files = files };

    [Fact]
    public async Task CreatesTwoCommitsInDependencyOrder()
    {
        _fixture!.WriteFile("src/a.txt", "a\n");
        _fixture!.WriteFile("src/b.txt", "b\n");

        var capture = await CaptureAsync();
        Assert.True(capture.Success);

        var outcome = await ExecuteAsync(capture.Inventory,
            Group("feat: b", "src/b.txt"),
            Group("feat: a", "src/a.txt"));

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Details!.Commits.Count);
        Assert.Equal(["feat: b", "feat: a"], outcome.Details.Commits.Select(c => c.Message).ToArray());
        Assert.Equal(outcome.Details.Commits[^1].Hash, _fixture.Head());
        Assert.Equal(0, outcome.Details.RemainingFiles.Count);
        Assert.Equal(0, _fixture.Status().Length);
    }

    [Fact]
    public async Task MidPipelineHookFailureLeavesFirstCommittedSecondReported()
    {
        _fixture!.WriteFile("one.txt", "1\n");
        _fixture!.WriteFile("two.txt", "2\n");
        // A pre-commit hook that fails only when two.txt is in the staged set.
        File.WriteAllText(Path.Combine(_fixture.RepoPath, ".git", "hooks", "pre-commit"),
            "#!/bin/sh\ngit diff --cached --name-only | grep -q 'two.txt' && exit 1 || exit 0\n");

        var capture = await CaptureAsync();

        var outcome = await ExecuteAsync(capture.Inventory,
            Group("first", "one.txt"),
            Group("second", "two.txt"));

        Assert.False(outcome.Success);
        Assert.Contains("git commit failed", outcome.Error);
        Assert.Single(outcome.Details!.Commits);
        Assert.Equal("first", outcome.Details.Commits[0].Message);
        Assert.Contains("two.txt", outcome.Details.RemainingFiles);
        // one.txt is committed; two.txt is still dirty in the worktree.
        Assert.Contains("two.txt", _fixture.Status());
    }

    [Fact]
    public async Task StagedSetMismatchAbortsBeforeCommit()
    {
        _fixture!.WriteFile("a.txt", "a\n");
        _fixture!.WriteFile("b.txt", "b\n");
        var capture = await CaptureAsync();

        // Commit b.txt out-of-band, leaving the captured inventory stale.
        _fixture.Add("b.txt");
        _fixture.Commit("b only");

        var outcome = await ExecuteAsync(capture.Inventory,
            Group("a", "a.txt"),
            Group("b", "b.txt"));

        Assert.False(outcome.Success);
        Assert.Contains("Staged set mismatch", outcome.Error);
        Assert.Equal(1, outcome.Details!.Commits.Count);
        Assert.Equal("a", outcome.Details.Commits[0].Message);
        Assert.Contains("b.txt", outcome.Details.RemainingFiles);
    }

    [Fact]
    public async Task LockfilesAreExcludedAndNeverCommitted()
    {
        _fixture!.WriteFile("src/app.cs", "code\n");
        _fixture!.WriteFile("package-lock.json", "{}\n");

        var capture = await CaptureAsync();
        Assert.Contains("package-lock.json", capture.Inventory!.ExcludedFiles);
        Assert.DoesNotContain(capture.Inventory.Changes, c => c.Path == "package-lock.json");

        var outcome = await ExecuteAsync(capture.Inventory, Group("feat: app", "src/app.cs"));

        Assert.True(outcome.Success);
        Assert.Contains("package-lock.json", outcome.Details!.ExcludedFiles);
        // The lockfile remains uncommitted and dirty.
        Assert.Contains("package-lock.json", _fixture.Status());
    }

    [Fact]
    public async Task DryRunChangesNothing()
    {
        _fixture!.WriteFile("a.txt", "a\n");
        var headBefore = _fixture.Head();
        var capture = await CaptureAsync();

        var outcome = await _executor.ExecuteAsync(new CommitExecutor.ExecuteRequest(
            _fixture!.RepoPath, capture.Inventory!, [Group("new", "a.txt")], DryRun: true));

        Assert.True(outcome.Success);
        Assert.True(outcome.Details!.WasDryRun);
        Assert.Equal(0, outcome.Details.Commits.Count);
        Assert.Contains("a.txt", outcome.Details.RemainingFiles);
        Assert.Equal(headBefore, _fixture.Head());
    }

    [Fact]
    public async Task RunHooksFalseAllowsCommitDespiteFailingHook()
    {
        _fixture!.WriteFile("a.txt", "a\n");
        // Hook that always fails.
        File.WriteAllText(Path.Combine(_fixture.RepoPath, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 1\n");
        var capture = await CaptureAsync();

        var outcome = await _executor.ExecuteAsync(new CommitExecutor.ExecuteRequest(
            _fixture!.RepoPath, capture.Inventory!, [Group("with hook", "a.txt")], RunHooks: false));

        Assert.True(outcome.Success);
        Assert.Single(outcome.Details!.Commits);

        // With runHooks == true (default), the same hook blocks the commit.
        _fixture.WriteFile("b.txt", "b\n");
        var capture2 = await CaptureAsync();
        var outcome2 = await _executor.ExecuteAsync(new CommitExecutor.ExecuteRequest(
            _fixture!.RepoPath, capture2.Inventory!, [Group("blocked", "b.txt")], RunHooks: true));

        Assert.False(outcome2.Success);
        Assert.Contains("git commit failed", outcome2.Error);
    }
}
