using System.Text.Json;
using Xunit;

namespace PiSharp.Git.Tests;

public sealed class CommitToolTests : IAsyncLifetime
{
    private GitFixture? _fixture;
    private CommitTool _tool = null!;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Web);

    public async Task InitializeAsync()
    {
        _fixture = new GitFixture();
        var runner = new GitRunner();
        var options = new GitPluginOptions();
        var classifier = new ChangeClassifier(options);
        var inventory = new CommitInventoryService(runner, classifier);
        var executor = new CommitExecutor(runner, new CommitGraph());
        _tool = new CommitTool(inventory, executor, options) { Cwd = _fixture.RepoPath };
        await Task.CompletedTask;
    }
    private static string Text(PiSharp.Agent.Core.Tools.AgentToolResult<object?> result)
        => string.Join("\n", result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Select(c => c.Text));


    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task InventoryCallReturnsChangesAndCommitsNothing()
    {
        _fixture!.WriteFile("src/a.cs", "a\n");
        _fixture!.WriteFile("src/b.cs", "b\n");
        var headBefore = _fixture.Head();

        var result = await _tool.ExecuteCoreAsync(Json(new { split = true }), CancellationToken.None);

        Assert.NotNull(result.Inventory);
        Assert.Equal(2, result.Inventory.Changes.Count);
        Assert.Contains("src/a.cs", result.Inventory.Changes.Select(c => c.Path));
        Assert.Null(result.Details);
        Assert.Equal(headBefore, _fixture.Head());
        Assert.Contains("main", Text(result.Output));
    }

    [Fact]
    public async Task ExecuteCallCommitsInDependencyOrder()
    {
        _fixture!.WriteFile("src/a.cs", "a\n");
        _fixture!.WriteFile("tests/a.Tests.cs", "t\n");

        var groups = new[]
        {
            new { message = "test: a tests", files = new[] { "tests/a.Tests.cs" }, id = "tests", dependsOn = new[] { "src" } },
            new { message = "feat: a", files = new[] { "src/a.cs" }, id = "src", dependsOn = Array.Empty<string>() }
        };
        var result = await _tool.ExecuteCoreAsync(Json(new { groups }), CancellationToken.None);


        Assert.NotNull(result!.Details);
        Assert.Equal(2, result.Details.Commits.Count);
        Assert.Equal(["feat: a", "test: a tests"], result.Details.Commits.Select(c => c.Message).ToArray());
        Assert.Equal(0, result.Details.RemainingFiles.Count);
        Assert.Equal(2, _fixture.CommittedMessages().Length - 1); // base + 2 new
    }

    [Fact]
    public async Task CycleRejectedWithZeroCommits()
    {
        _fixture!.WriteFile("a.cs", "a\n");
        _fixture!.WriteFile("b.cs", "b\n");

        var groups = new[]
        {
            new { message = "a", files = new[] { "a.cs" }, id = "a", dependsOn = new[] { "b" } },
            new { message = "b", files = new[] { "b.cs" }, id = "b", dependsOn = new[] { "a" } }
        };
        var result = await _tool.ExecuteCoreAsync(Json(new { groups }), CancellationToken.None);

        Assert.NotNull(result.Details);
        Assert.NotNull(result.Details.RejectedCycle);
        Assert.Equal(0, result.Details.Commits.Count);
        Assert.Contains("cycle", Text(result.Output), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _fixture.CommittedMessages().Length); // only the base commit
    }

    [Fact]
    public async Task SingleCommitModeCreatesOneAtomicCommit()
    {
        _fixture!.WriteFile("a.cs", "a\n");
        _fixture!.WriteFile("b.cs", "b\n");

        var result = await _tool.ExecuteCoreAsync(Json(new { message = "feat: everything", split = false }), CancellationToken.None);

        Assert.NotNull(result.Details);
        var commit = Assert.Single(result.Details.Commits);
        Assert.Equal("feat: everything", commit.Message);
        Assert.Equal(2, commit.Files.Count);
        Assert.Equal(0, _fixture.Status().Length);
    }

    [Fact]
    public async Task MessageWithoutSplitFalseIsRejected()
    {
        var result = await _tool.ExecuteCoreAsync(Json(new { message = "feat: x" }), CancellationToken.None);

        Assert.Null(result.Details);
        Assert.Contains("requires Split=false", result.Output.Content[0].ToString());
    }

    [Fact]
    public async Task DryRunReportsRemainingWithoutCommitting()
    {
        _fixture!.WriteFile("a.cs", "a\n");
        var headBefore = _fixture.Head();

        var result = await _tool.ExecuteCoreAsync(Json(new
        {
            groups = new[] { new { message = "feat: a", files = new[] { "a.cs" } } },
            dryRun = true
        }), CancellationToken.None);

        Assert.NotNull(result.Details);
        Assert.True(result.Details.WasDryRun);
        Assert.Equal(0, result.Details.Commits.Count);
        Assert.Contains("a.cs", result.Details.RemainingFiles);
        Assert.Equal(headBefore, _fixture.Head());
    }

    [Fact]
    public async Task NonRepoReturnsActionableError()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pisharp-no-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            _tool.Cwd = temp;
            var result = await _tool.ExecuteCoreAsync(Json(new { split = true }), CancellationToken.None);
            Assert.Contains("Not a git repository", result.Output.Content[0].ToString());
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
