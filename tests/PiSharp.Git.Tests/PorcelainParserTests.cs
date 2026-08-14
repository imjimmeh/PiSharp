using Xunit;

namespace PiSharp.Git.Tests;

public sealed class PorcelainParserTests
{
    [Fact]
    public void ParsesModifiedUntrackedAndDeleted()
    {
        // " D a.txt\0?? b.txt\0?? dir/f.txt\0" — worktree deletion + two untracked.
        var output = " D a.txt\0?? b.txt\0?? dir/f.txt\0";
        var items = PorcelainParser.Parse(output);

        Assert.Equal(3, items.Count);
        Assert.Equal(" D", items[0].Status);
        Assert.Equal("a.txt", items[0].Path);
        Assert.False(items[0].IsRename);

        Assert.Equal("??", items[1].Status);
        Assert.Equal("b.txt", items[1].Path);
        Assert.True(items[1].IsUntracked);

        Assert.Equal("dir/f.txt", items[2].Path);
    }

    [Fact]
    public void ParsesStagedAndMixedStatuses()
    {
        var output = "M  a.txt\0MM b.txt\0A  c.txt\0";
        var items = PorcelainParser.Parse(output);

        Assert.Equal(3, items.Count);
        Assert.Equal("M ", items[0].Status);
        Assert.True(items[0].IsStaged);
        Assert.False(items[0].IsUnstaged);

        Assert.Equal("MM", items[1].Status);
        Assert.True(items[1].IsStaged);
        Assert.True(items[1].IsUnstaged);
    }

    [Fact]
    public void ParsesRenameWithDestinationFirst()
    {
        // porcelain -z emits destination FIRST, then original (verified empirically).
        var output = "RM c.txt\0b.txt\0";
        var items = PorcelainParser.Parse(output);

        var item = Assert.Single(items);
        Assert.True(item.IsRename);
        Assert.Equal("c.txt", item.Path);
        Assert.Equal("b.txt", item.RenameSource);
    }

    [Fact]
    public void ParsesRenameWithoutAdditionalWorktreeChange()
    {
        var output = "R  new.txt\0old.txt\0";
        var items = PorcelainParser.Parse(output);

        var item = Assert.Single(items);
        Assert.True(item.IsRename);
        Assert.Equal("new.txt", item.Path);
        Assert.Equal("old.txt", item.RenameSource);
    }

    [Theory]
    [InlineData("UU", true)]
    [InlineData("AA", true)]
    [InlineData("DD", true)]
    [InlineData("AU", true)]
    [InlineData("UA", true)]
    [InlineData("DU", true)]
    [InlineData("UD", true)]
    [InlineData("M ", false)]
    [InlineData("??", false)]
    [InlineData("A ", false)]
    [InlineData("R ", false)]
    public void IsUnmergedDetectsConflictStates(string status, bool expected)
    {
        Assert.Equal(expected, PorcelainParser.IsUnmerged(status));
    }

    [Fact]
    public void IgnoresTrailingAndEmptyTokens()
    {
        var output = "?? a.txt\0\0\0";
        Assert.Single(PorcelainParser.Parse(output));
    }
}
