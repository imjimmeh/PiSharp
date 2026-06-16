using PiSharp.Tools.Shared;
using Xunit;

namespace PiSharp.Tools.Tests.Shared;

public sealed class TruncationTests
{
    [Fact]
    public void TruncateHeadKeepsCompleteLinesWithinByteLimit()
    {
        var result = Truncation.TruncateHead("alpha\nbeta\ngamma", new TruncationOptions(MaxLines: 10, MaxBytes: 11));
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
        Assert.Equal("alpha\nbeta", result.Content);
    }

    [Fact]
    public void TruncateTailReturnsEndOfSingleLongLine()
    {
        var result = Truncation.TruncateTail("abcdef", new TruncationOptions(MaxLines: 10, MaxBytes: 3));
        Assert.True(result.Truncated);
        Assert.True(result.LastLinePartial);
        Assert.Equal("def", result.Content);
    }

    [Fact]
    public void FormatSizeMatchesToolOutputStyle()
    {
        Assert.Equal("50.0KB", Truncation.FormatSize(50 * 1024));
    }
}
