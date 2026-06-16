using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class PromptTextBufferTests
{
    [Fact]
    public void NormalizeConvertsAllCarriageReturnsToLineFeeds()
    {
        Assert.Equal("a\nb\nc", PromptTextBuffer.Normalize("a\r\nb\rc"));
    }

    [Fact]
    public void InsertNormalizesTextAndAdvancesCursorFromClampedOffset()
    {
        var result = PromptTextBuffer.Insert("ab", 1, "x\r\ny");

        Assert.Equal("ax\nyb", result.Text);
        Assert.Equal(4, result.CursorOffset);
    }

    [Fact]
    public void InsertClampsCursorOffsetBeforeMutating()
    {
        var result = PromptTextBuffer.Insert("ab", 99, "c");

        Assert.Equal("abc", result.Text);
        Assert.Equal(3, result.CursorOffset);
    }

    [Fact]
    public void DeleteLeftRemovesCharacterBeforeCursor()
    {
        var result = PromptTextBuffer.DeleteLeft("abc", 2);

        Assert.Equal("ac", result.Text);
        Assert.Equal(1, result.CursorOffset);
    }

    [Fact]
    public void DeleteLeftAtStartIsNoOp()
    {
        var result = PromptTextBuffer.DeleteLeft("abc", 0);

        Assert.Equal("abc", result.Text);
        Assert.Equal(0, result.CursorOffset);
    }

    [Fact]
    public void DeleteRightRemovesCharacterAtCursor()
    {
        var result = PromptTextBuffer.DeleteRight("abc", 1);

        Assert.Equal("ac", result.Text);
        Assert.Equal(1, result.CursorOffset);
    }

    [Fact]
    public void DeleteRightAtEndIsNoOp()
    {
        var result = PromptTextBuffer.DeleteRight("abc", 3);

        Assert.Equal("abc", result.Text);
        Assert.Equal(3, result.CursorOffset);
    }
}
