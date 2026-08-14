using PiSharp.Ast.Hash;
using PiSharp.Ast.Tests.Fakes;
using PiSharp.Ast.Tools;
using PiSharp.Tools.Edit;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class HashlineEditToolTests
{
    private static async Task<string> RunEditAsync(FakeExecutionEnv env, HashlineEditInput input)
    {
        var tool = new HashlineEditTool(env);
        var result = await tool.ExecuteAsync("tc1", input);
        Assert.NotNull(result.Details);
        return result.Details!.Diff;
    }

    [Fact]
    public async Task ReplaceByAnchorDerivesOldTextFromContent()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\ntwo\nthree\n");
        var index = new HashLineIndex("one\ntwo\nthree\n");

        await RunEditAsync(env, new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(AnchorHash: index.AnchorHash(2), NewText: "TWO")
        ]));

        Assert.Equal("one\nTWO\nthree\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task MultiLineBlockReplaceByAnchor()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "line1\nline2\nline3\nline4\n");
        var index = new HashLineIndex("line1\nline2\nline3\nline4\n");

        await RunEditAsync(env, new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(AnchorHash: index.AnchorHash(2, 2), AnchorLineCount: 2, NewText: "X")
        ]));

        Assert.Equal("line1\nX\nline4\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task InsertBeforeAndAfterAnchor()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "a\nbb\nc\n");
        var index = new HashLineIndex("a\nbb\nc\n");

        await RunEditAsync(env, new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(AnchorHash: index.AnchorHash(2), NewText: "before", Placement: "insert_before")
        ]));
        await RunEditAsync(env, new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(AnchorHash: index.AnchorHash(2), NewText: "after", Placement: "insert_after")
        ]));

        Assert.Equal("a\nbefore\nbb\nafter\nc\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task AnchorNotFoundThrowsStaleErrorAndWritesNothing()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "original content\n");
        // Anchor of a line that no longer exists (file changed after the anchor was read).
        var staleIndex = new HashLineIndex("different\ncontent\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(AnchorHash: staleIndex.AnchorHash(1), NewText: "X")
            ])));

        Assert.Contains("stale anchor", error.Message);
        Assert.Equal("original content\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task AmbiguousAnchorThrowsWithCandidateLines()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "dup\ndup\nunique\n");
        var index = new HashLineIndex("dup\ndup\nunique\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(AnchorHash: index.AnchorHash(1), NewText: "X")
            ])));

        Assert.Contains("anchor is ambiguous", error.Message);
        Assert.Contains("1, 2", error.Message);
        Assert.Equal("dup\ndup\nunique\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task BothOldTextAndAnchorHashRejected()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(OldText: "one", AnchorHash: "0123456789ab", NewText: "X")
            ])));
        Assert.Contains("exactly one addressing mode", error.Message);
    }

    [Fact]
    public async Task NeitherOldTextNorAnchorHashRejected()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(NewText: "X")
            ])));
        Assert.Contains("either oldText or anchorHash", error.Message);
    }

    [Fact]
    public async Task MalformedAnchorHashRejected()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(AnchorHash: "not-hex", NewText: "X")
            ])));
        Assert.Contains("12+ hex characters", error.Message);
    }

    [Fact]
    public async Task InvalidPlacementRejected()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\n");
        var index = new HashLineIndex("one\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(AnchorHash: index.AnchorHash(1), NewText: "X", Placement: "sideways")
            ])));
        Assert.Contains("placement", error.Message);
    }

    [Fact]
    public async Task CrLfFileKeepsCrLf()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\r\ntwo\r\nthree\r\n");
        var index = new HashLineIndex("one\r\ntwo\r\nthree\r\n");

        await RunEditAsync(env, new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(AnchorHash: index.AnchorHash(2), NewText: "TWO")
        ]));

        Assert.Equal("one\r\nTWO\r\nthree\r\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task DiffOutputMatchesEditToolDetailsShape()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\ntwo\nthree\n");
        var index = new HashLineIndex("one\ntwo\nthree\n");
        var tool = new HashlineEditTool(env);
        var result = await tool.ExecuteAsync("tc1", new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(AnchorHash: index.AnchorHash(1), NewText: "ONE")
        ]));

        Assert.NotNull(result.Details);
        Assert.Contains("-1 one", result.Details!.Diff);
        Assert.Contains("+1 ONE", result.Details.Diff);
        Assert.Equal(1, result.Details.FirstChangedLine);
    }

    [Fact]
    public async Task OldTextAddressingIsByteIdenticalToBuiltInEditTool()
    {
        var env1 = new FakeExecutionEnv();
        env1.AddFile("sample.cs", "alpha\nbeta\ngamma\n");
        var env2 = new FakeExecutionEnv();
        env2.AddFile("sample.cs", "alpha\nbeta\ngamma\n");

        var overrideTool = new HashlineEditTool(env1);
        await overrideTool.ExecuteAsync("tc1", new HashlineEditInput("sample.cs",
        [
            new HashlineEditReplacement(OldText: "beta", NewText: "BETA")
        ]));

        var builtInTool = new EditTool(env2);
        await builtInTool.ExecuteAsync("tc1", new EditToolInput("sample.cs",
        [
            new EditReplacement("beta", "BETA")
        ]));

        Assert.Equal(env2.ReadFileOrDefault("sample.cs"), env1.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task ConcurrentEditsSerializeThroughFileMutationQueue()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "a\nb\nc\n");

        var tool = new HashlineEditTool(env);
        var first = tool.ExecuteAsync("tc1", new HashlineEditInput("sample.cs",
            [new HashlineEditReplacement(OldText: "a", NewText: "A")]));
        var second = tool.ExecuteAsync("tc2", new HashlineEditInput("sample.cs",
            [new HashlineEditReplacement(OldText: "b", NewText: "B")]));

        await Task.WhenAll(first, second);

        // Each call re-reads inside the queue, so both edits land (no lost update, no interleaving).
        Assert.Equal("A\nB\nc\n", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task OverlappingAnchoredEditsRejected()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", "one\ntwo\nthree\n");
        var index = new HashLineIndex("one\ntwo\nthree\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HashlineEditTool(env).ExecuteAsync(
            "tc1", new HashlineEditInput("sample.cs",
            [
                new HashlineEditReplacement(AnchorHash: index.AnchorHash(1, 2), AnchorLineCount: 2, NewText: "X"),
                new HashlineEditReplacement(AnchorHash: index.AnchorHash(2), NewText: "Y")
            ])));

        Assert.Contains("overlap", error.Message);
        Assert.Equal("one\ntwo\nthree\n", env.ReadFileOrDefault("sample.cs"));
    }
}
