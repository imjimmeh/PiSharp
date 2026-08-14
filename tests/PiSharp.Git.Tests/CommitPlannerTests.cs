using Xunit;

namespace PiSharp.Git.Tests;

public sealed class CommitPlannerTests
{
    private static ChangeItem Item(string path, ChangeCategory category, int score)
        => new(path, "M ", category, score, false, null);

    private static ChangeInventory Inventory(params ChangeItem[] items)
        => new("abc123", "main", items, []);

    [Fact]
    public void EmptyInventoryYieldsNoGroups()
    {
        var plan = new CommitPlanner().Plan(Inventory());
        Assert.True(plan.Success);
        Assert.Equal(0, plan.Groups!.Count);
    }

    [Fact]
    public void ClustersByCategoryAndTopLevelDirectory()
    {
        var inventory = Inventory(
            Item("src/A.cs", ChangeCategory.Source, 4),
            Item("src/B.cs", ChangeCategory.Source, 4),
            Item("tests/A.Tests.cs", ChangeCategory.Test, 3),
            Item("README.md", ChangeCategory.Docs, 2));

        var plan = new CommitPlanner().Plan(inventory);

        Assert.Equal(3, plan.Groups!.Count);
        var src = plan.Groups.First(g => g.Scope == "src" && g.Category == ChangeCategory.Source);
        Assert.Equal(["src/A.cs", "src/B.cs"], src.Files.OrderBy(f => f));
    }

    [Fact]
    public void TestGroupDependsOnMatchingSourceGroup()
    {
        var inventory = Inventory(
            Item("src/Foo.cs", ChangeCategory.Source, 4),
            Item("tests/Foo.Tests.cs", ChangeCategory.Test, 3));

        var plan = new CommitPlanner().Plan(inventory);

        var testGroup = plan.Groups!.Single(g => g.Category == ChangeCategory.Test);
        Assert.NotEmpty(testGroup.DependsOn);
        // DependsOn is an index into the groups list pointing at the source group.
        var sourceGroup = plan.Groups[testGroup.DependsOn[0]];
        Assert.Equal(ChangeCategory.Source, sourceGroup.Category);
    }

    [Fact]
    public void SourceAndTestGroupedSeparately()
    {
        var inventory = Inventory(
            Item("src/A.cs", ChangeCategory.Source, 4),
            Item("src/A.Tests.cs", ChangeCategory.Test, 3));

        var plan = new CommitPlanner().Plan(inventory);

        // Different categories -> different groups.
        Assert.Equal(2, plan.Groups!.Count);
    }

    [Fact]
    public void DraftMessageUsesConventionalPrefix()
    {
        var planner = new CommitPlanner();
        var msg = planner.DraftMessage(ChangeCategory.Source, "src", ["src/A.cs"]);

        Assert.Equal("feat(src): A (+1 files)", msg);
    }

    [Fact]
    public void DraftMessageOmitsScopeAtRepoRoot()
    {
        var planner = new CommitPlanner();
        var msg = planner.DraftMessage(ChangeCategory.Docs, "", ["README.md"]);

        Assert.Equal("docs: README (+1 files)", msg);
    }

    [Fact]
    public void DraftMessageDisablesPrefixWhenConfigured()
    {
        var planner = new CommitPlanner();
        var msg = planner.DraftMessage(ChangeCategory.Test, "tests", ["tests/A.Tests.cs"], conventionalPrefix: false);

        Assert.Equal("A.Tests (+1 files)", msg);
    }

    [Fact]
    public void GroupsOrderByScoreDescending()
    {
        var inventory = Inventory(
            Item("docs/README.md", ChangeCategory.Docs, 2),
            Item("src/Foo.cs", ChangeCategory.Source, 4),
            Item("tests/Foo.Tests.cs", ChangeCategory.Test, 3));

        var plan = new CommitPlanner().Plan(inventory);

        // Source(4) first, then Test(3), then Docs(2).
        Assert.True(plan.Groups![0].Category == ChangeCategory.Source);
        Assert.True(plan.Groups![1].Category == ChangeCategory.Test);
        Assert.True(plan.Groups![2].Category == ChangeCategory.Docs);
    }
}
