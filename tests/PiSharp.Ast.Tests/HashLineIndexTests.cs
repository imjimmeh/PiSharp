using PiSharp.Ast.Hash;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class HashLineIndexTests
{
    [Fact]
    public void AnchorIsStableAcrossInstances()
    {
        var a = new HashLineIndex("one\ntwo\nthree\n");
        var b = new HashLineIndex("one\ntwo\nthree\n");
        Assert.Equal(a.AnchorHash(1), b.AnchorHash(1));
        Assert.Equal(a.AnchorHash(2), b.AnchorHash(2));
        Assert.Equal(a.AnchorHash(3), b.AnchorHash(3));
    }

    [Fact]
    public void AnchorIsStableAcrossCrLfAndLf()
    {
        var lf = new HashLineIndex("one\ntwo\nthree\n");
        var crlf = new HashLineIndex("one\r\ntwo\r\nthree\r\n");
        for (var line = 1; line <= 3; line++)
        {
            Assert.Equal(lf.AnchorHash(line), crlf.AnchorHash(line));
        }
    }

    [Fact]
    public void TwelveHexPrefixResolvesToFullHashLine()
    {
        var index = new HashLineIndex("alpha\nbeta\n");
        var full = index.BlockHash(1);
        var prefix = full[..12];
        Assert.Equal(12, prefix.Length);
        var result = index.Resolve(prefix);
        Assert.True(result.Found);
        Assert.Equal(1, result.Resolution!.StartLine);
        Assert.Equal("alpha", result.Resolution.BlockText);
        Assert.Equal(full, result.Resolution.FullHash);
    }

    [Fact]
    public void BlockHashIsLfJoinedLinesWithoutTrailingNewline()
    {
        var index = new HashLineIndex("a\nb\nc\n");
        var expected = ContentHasher.Sha256Hex("a\nb");
        Assert.Equal(expected, index.BlockHash(1, lineCount: 2));
        // Multi-line block text joins with '\n' and no trailing newline.
        var resolve = index.Resolve(ContentHasher.Anchor("a\nb"), lineCount: 2);
        Assert.True(resolve.Found);
        Assert.Equal("a\nb", resolve.Resolution!.BlockText);
    }

    [Fact]
    public void AnchorLineCountGreaterThanOneResolvesBlock()
    {
        var index = new HashLineIndex("line1\nline2\nline3\n");
        var anchor = index.AnchorHash(2, lineCount: 2);
        var result = index.Resolve(anchor, lineCount: 2);
        Assert.True(result.Found);
        Assert.Equal(2, result.Resolution!.StartLine);
        Assert.Equal("line2\nline3", result.Resolution.BlockText);
    }

    [Fact]
    public void ZeroMatchReportsNotFound()
    {
        var index = new HashLineIndex("alpha\nbeta\n");
        var result = index.Resolve("ffffffffffff");
        Assert.False(result.Found);
        Assert.Null(result.Resolution);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void AmbiguousAnchorReportsCandidateLines()
    {
        var index = new HashLineIndex("dup\ndup\nunique\n");
        var anchor = index.AnchorHash(1);
        var result = index.Resolve(anchor);
        Assert.False(result.Found);
        Assert.Contains("ambiguous", result.Error);
        Assert.Equal(new[] { 1, 2 }, result.AmbiguousLines);
    }

    [Fact]
    public void AnchorsChangeWhenContentChanges()
    {
        var before = new HashLineIndex("same\nvalue\n");
        var after = new HashLineIndex("same\nvalue changed\n");
        Assert.NotEqual(before.AnchorHash(2), after.AnchorHash(2));
        Assert.Equal(before.AnchorHash(1), after.AnchorHash(1));
    }

    [Fact]
    public void TrailingNewlineDoesNotCreatePhantomLine()
    {
        Assert.Equal(3, new HashLineIndex("a\nb\nc\n").LineCount);
        Assert.Equal(3, new HashLineIndex("a\nb\nc").LineCount);
        Assert.Equal(0, new HashLineIndex(string.Empty).LineCount);
    }

    [Fact]
    public void ResolveMatchesCaseInsensitively()
    {
        var index = new HashLineIndex("alpha\n");
        var anchor = index.AnchorHash(1).ToUpperInvariant();
        Assert.True(index.Resolve(anchor).Found);
    }
}
