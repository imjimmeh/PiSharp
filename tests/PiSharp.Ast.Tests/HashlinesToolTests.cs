using PiSharp.Ast.Hash;
using System.Text.RegularExpressions;
using PiSharp.Ast.Tests.Fakes;
using PiSharp.Ast.Tools;
using PiSharp.Tools.Edit;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class HashlinesToolTests
{
    private static readonly Regex AnchorShape = new(@"^@[0-9a-f]{12}  \d+  (.*)$", RegexOptions.Compiled);

    [Fact]
    public async Task RendersAnchorLineNumberAndContentPerLine()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\ntwo\nthree\n");
        var tool = new HashlinesTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlinesInput("sample.cs"));

        var lines = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text.Split('\n');
        Assert.Equal(3, lines.Length);

        var index = new HashLineIndex("one\ntwo\nthree\n");
        Assert.Equal($"@{index.AnchorHash(1)}  1  one", lines[0]);
        Assert.Equal($"@{index.AnchorHash(2)}  2  two", lines[1]);
        Assert.Equal($"@{index.AnchorHash(3)}  3  three", lines[2]);
        foreach (var line in lines)
        {
            Assert.Matches(AnchorShape, line);
        }
    }

    [Fact]
    public async Task AnchorMatchesContentHashOfLine()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "private void Reconnect() { }\n");
        var tool = new HashlinesTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlinesInput("sample.cs"));
        var rendered = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text;
        var anchor = rendered[1..13];
        Assert.Equal(ContentHasher.Anchor("private void Reconnect() { }"), anchor);
    }

    [Fact]
    public async Task OffsetAndLimitSelectLineWindow()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", string.Join('\n', Enumerable.Range(1, 5).Select(i => $"line{i}")));
        var tool = new HashlinesTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlinesInput("sample.cs", Offset: 2, Limit: 2));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text;
        var lines = text.Split('\n');
        Assert.EndsWith("  line2", lines[0]);
        Assert.EndsWith("  line3", lines[1]);
        Assert.Contains("2 more lines in file. Use offset=4 to continue.", text);
    }

    [Fact]
    public async Task LimitBelowFileLengthReportsRemainingLines()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", string.Join('\n', Enumerable.Range(1, 5).Select(i => $"line{i}")));
        var tool = new HashlinesTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlinesInput("sample.cs", Limit: 2));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text;
        Assert.Contains("3 more lines in file. Use offset=3 to continue.", text);
    }

    [Fact]
    public async Task LargeRenderIsTruncatedWithMetadata()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("big.cs", string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"line{i}")));
        var tool = new HashlinesTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlinesInput("big.cs", Limit: 3000));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text;
        Assert.Contains("[Showing lines 1-", text);
        Assert.Contains("of 3000", text);
        Assert.Contains("Use offset=", text);
        Assert.NotNull(result.Details);
        Assert.True(result.Details.Truncation!.Truncated);
        Assert.True(result.Details.LinesTruncated);
    }

    [Fact]
    public async Task MissingFileThrowsFileError()
    {
        var env = new FakeExecutionEnv();
        var tool = new HashlinesTool(env);
        var error = await Assert.ThrowsAsync<PiSharp.Abstractions.Errors.FileError>(
            () => tool.ExecuteAsync("tc1", new HashlinesInput("missing.cs")));
        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OffsetBeyondEndThrows()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\n");
        var tool = new HashlinesTool(env);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.ExecuteAsync("tc1", new HashlinesInput("sample.cs", Offset: 5)));
        Assert.Contains("beyond end of file", error.Message);
    }

    [Fact]
    public async Task CrLfFileRendersSameAnchorsAsLf()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("crlf.cs", "one\r\ntwo\r\n");
        var tool = new HashlinesTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlinesInput("crlf.cs"));
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text;

        var lfIndex = new HashLineIndex("one\ntwo\n");
        var lines = text.Split('\n');
        Assert.Equal($"@{lfIndex.AnchorHash(1)}  1  one", lines[0]);
        Assert.Equal($"@{lfIndex.AnchorHash(2)}  2  two", lines[1]);
    }
}
