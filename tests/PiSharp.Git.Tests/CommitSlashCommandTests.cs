using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Git.Tests;

public sealed class CommitSlashCommandTests : IAsyncLifetime
{
    private GitFixture? _fixture;
    private FakeUi _ui = null!;
    private List<string> _messages = null!;
    private CommitSlashCommand _command = null!;

    public async Task InitializeAsync()
    {
        _fixture = new GitFixture();
        _ui = new FakeUi();
        _messages = [];
        var runner = new GitRunner();
        var options = new GitPluginOptions();
        var classifier = new ChangeClassifier(options);
        var inventoryService = new CommitInventoryService(runner, classifier);
        var planner = new CommitPlanner();
        var executor = new CommitExecutor(runner, new CommitGraph());
        var host = new CommandHost(_ui, HasUi: true, _fixture.RepoPath, (text, _) =>
        {
            _messages.Add(text);
            return Task.CompletedTask;
        });
        _command = new CommitSlashCommand(host, inventoryService, planner, executor, options);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task SingleCommitModeCommitsWithGivenMessage()
    {
        _fixture!.WriteFile("a.cs", "a\n");
        _fixture!.WriteFile("b.cs", "b\n");

        await _command.HandleAsync("--yes --no-split --message \"chore: everything\"");

        Assert.Single(_fixture.CommittedMessages().Skip(1));
        Assert.Equal("chore: everything", _fixture.CommittedMessages()[0]);
        Assert.Contains(_messages, m => m.Contains("Committed 1 change"));
        Assert.Equal(0, _fixture.Status().Length);
    }

    [Fact]
    public async Task AutoPlanCommitsDependencyOrderedGroups()
    {
        _fixture!.WriteFile("src/Foo.cs", "code\n");
        _fixture!.WriteFile("tests/Foo.Tests.cs", "test\n");

        await _command.HandleAsync("--yes");

        var commits = _fixture.CommittedMessages().Reverse().Skip(1).ToArray();
        Assert.Equal(2, commits.Length);
        // Source commit before its test commit (chronological order).
        Assert.Contains("feat", commits[0]);
        Assert.Contains("test", commits[1]);
        Assert.Contains(_messages, m => m.Contains("Commit plan"));
        Assert.Equal(0, _fixture.Status().Length);
    }

    [Fact]
    public async Task DryRunPrintsPlanAndCommitsNothing()
    {
        _fixture!.WriteFile("a.cs", "a\n");
        var headBefore = _fixture.Head();

        await _command.HandleAsync("--dry-run");

        Assert.Contains(_messages, m => m.Contains("Commit plan"));
        Assert.Contains(_ui.Notifications, n => n.Message.Contains("Dry run"));
        Assert.Equal(headBefore, _fixture.Head());
    }

    [Fact]
    public async Task ConfirmationDeclinedCancelsEverything()
    {
        _fixture!.WriteFile("a.cs", "a\n");
        _ui.ConfirmResult = false;

        await _command.HandleAsync("");

        Assert.Contains(_ui.Notifications, n => n.Message == "Commit cancelled by user.");
        Assert.Equal(1, _fixture.CommittedMessages().Length); // base only
    }

    [Fact]
    public async Task InputEditOverridesDraftMessage()
    {
        _fixture!.WriteFile("src/Foo.cs", "code\n");
        _ui.InputResult = "feat: edited message";

        await _command.HandleAsync("");

        Assert.Contains(_ui.InputPrompts, p => p.Contains("Commit message"));
        Assert.Equal("feat: edited message", _fixture.CommittedMessages()[0]);
    }

    [Fact]
    public async Task MessageWithoutNoSplitIsUsageError()
    {
        await _command.HandleAsync("--message \"chore: x\"");
        Assert.Contains(_ui.Notifications, n => n.Message.Contains("--message requires --no-split"));
    }

    [Fact]
    public async Task NothingToCommitNotifies()
    {
        await _command.HandleAsync("--yes");
        Assert.Contains(_ui.Notifications, n => n.Message == "Nothing to commit.");
    }

    [Fact]
    public async Task FilesFlagScopesTheInventory()
    {
        _fixture!.WriteFile("src/keep.cs", "k\n");
        _fixture!.WriteFile("src/skip.cs", "s\n");

        await _command.HandleAsync("--yes --no-split --message \"chore: keep only\" --files src/keep.cs");

        Assert.Equal("chore: keep only", _fixture.CommittedMessages()[0]);
        // skip.cs remains dirty.
        Assert.Contains("src/skip.cs", _fixture.Status());
    }
}
