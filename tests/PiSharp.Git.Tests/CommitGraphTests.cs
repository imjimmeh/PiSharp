using Xunit;

namespace PiSharp.Git.Tests;

public sealed class CommitGraphTests
{
    private static ChangeItem Item(string path, ChangeCategory category = ChangeCategory.Source, int? score = null)
        => new(path, "M ", category, score ?? ChangeClassifier.Score(category), false, null);

    private static CommitGroupInput Group(string message, string[] files, string? id = null, string[]? dependsOn = null)
        => new() { Message = message, Files = files, Id = id, DependsOn = dependsOn };

    private static CommitGraph.GraphResult Build(ChangeItem[] inventory, CommitGroupInput[] groups)
        => new CommitGraph().BuildAndOrder(groups, inventory);

    [Fact]
    public void ValidDagOrdersByScoreThenIndex()
    {
        var groups = new[]
        {
            Group("tests", ["src/A.Tests.cs"]),
            Group("src", ["src/A.cs"]),
            Group("docs", ["README.md"], dependsOn: ["0"])
        };
        var inventory = new[]
        {
            Item("src/A.cs", ChangeCategory.Source),      // score 4
            Item("src/A.Tests.cs", ChangeCategory.Test),  // score 3
            Item("README.md", ChangeCategory.Docs)        // score 2
        };

        var result = Build(inventory, groups);

        Assert.True(result.IsValid);
        // src(score 4) and tests(score 3) are both ready — src first (higher score, then index).
        // docs depends on tests (id "0"), so docs must come last.
        Assert.Equal(["1", "0", "2"], result.Ordered!.Select(g => g.Id));
    }

    [Fact]
    public void RespectsExplicitDependencies()
    {
        var groups = new[]
        {
            Group("b", ["b.cs"], id: "b"),
            Group("a", ["a.cs"], id: "a", dependsOn: ["b"]),
            Group("c", ["c.cs"], id: "c", dependsOn: ["a"])
        };
        var inventory = new[] { Item("a.cs"), Item("b.cs"), Item("c.cs") };

        var result = Build(inventory, groups);

        Assert.True(result.IsValid);
        var order = result.Ordered!.Select(g => g.Id).ToList();
        Assert.True(order.IndexOf("b") < order.IndexOf("a"));
        Assert.True(order.IndexOf("a") < order.IndexOf("c"));
    }

    [Fact]
    public void RejectsSelfLoop()
    {
        var groups = new[] { Group("a", ["a.cs"], "a", ["a"]) };
        var result = Build([Item("a.cs")], groups);
        Assert.False(result.IsValid);
        Assert.Contains("depends on itself", result.Error);
    }

    [Fact]
    public void RejectsThreeCycleWithConcretePath()
    {
        var groups = new[]
        {
            Group("0", ["0.cs"], "0", ["2"]),
            Group("1", ["1.cs"], "1", ["0"]),
            Group("2", ["2.cs"], "2", ["1"])
        };
        var inventory = new[] { Item("0.cs"), Item("1.cs"), Item("2.cs") };

        var result = Build(inventory, groups);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Cycle);
        Assert.Contains("0", result.Cycle);
        Assert.Contains("1", result.Cycle);
        Assert.Contains("2", result.Cycle);
    }

    [Fact]
    public void RejectsFileInTwoGroups()
    {
        var groups = new[]
        {
            Group("a", ["shared.cs"]),
            Group("b", ["shared.cs"])
        };
        var result = Build([Item("shared.cs")], groups);
        Assert.False(result.IsValid);
        Assert.Contains("exactly one group", result.Error);
    }

    [Fact]
    public void RejectsUncoveredInventoryFile()
    {
        var groups = new[] { Group("a", ["a.cs"]) };
        var inventory = new[] { Item("a.cs"), Item("b.cs") };
        var result = Build(inventory, groups);
        Assert.False(result.IsValid);
        Assert.Contains("not covered", result.Error);
    }

    [Fact]
    public void RejectsUnknownDependsOnId()
    {
        var groups = new[]
        {
            Group("a", ["a.cs"]),
            Group("b", ["b.cs"], dependsOn: ["nope"])
        };
        var result = Build([Item("a.cs"), Item("b.cs")], groups);
        Assert.False(result.IsValid);
        Assert.Contains("unknown group id", result.Error);
    }

    [Fact]
    public void RejectsFileNotInInventory()
    {
        var groups = new[] { Group("a", ["ghost.cs"]) };
        var result = Build([Item("a.cs")], groups);
        Assert.False(result.IsValid);
        Assert.Contains("not in the change inventory", result.Error);
    }

    [Fact]
    public void RejectsRenameSourceSplitAcrossGroup()
    {
        var rename = new ChangeItem("new.cs", "R ", ChangeCategory.Source, 4, true, "old.cs");
        var groups = new[]
        {
            Group("a", ["new.cs"]),
            Group("b", ["old.cs"])
        };
        var result = Build([rename], groups);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsEmptyGroups()
    {
        var result = Build([], []);
        Assert.False(result.IsValid);
        Assert.Contains("At least one commit group", result.Error);
    }

    [Fact]
    public void DuplicateDependencyEdgeIsCountedOnce()
    {
        var groups = new[]
        {
            Group("a", ["a.cs"], id: "a"),
            Group("b", ["b.cs"], id: "b", dependsOn: ["a", "a"])
        };
        var result = Build([Item("a.cs"), Item("b.cs")], groups);
        Assert.True(result.IsValid);
    }
}
