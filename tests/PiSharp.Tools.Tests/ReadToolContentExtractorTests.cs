using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Tools.Files;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

/// <summary>
/// Verifies the P28 content-extractor seam in <see cref="ReadTool"/>: the
/// extractor is consulted between image detection and the UTF-8 fallback, its
/// text routes through the normal offset/limit/truncation processing, null
/// results and exceptions degrade to the UTF-8 fallback, and files with no
/// extractor behave exactly as before.
/// </summary>
public sealed class ReadToolContentExtractorTests
{
    private sealed class StubExtractor(
        Func<string, byte[], FileContentExtractionResult?> handler,
        string id = "stub",
        Func<string, bool>? canHandle = null) : IFileContentExtractor
    {
        public string Id => id;
        public bool CanHandle(string path, ReadOnlySpan<byte> bytes) => canHandle?.Invoke(path) ?? true;
        public Task<FileContentExtractionResult?> ExtractAsync(string path, ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
            => Task.FromResult(handler(path, bytes.ToArray()));
    }

    private static string ExecuteText(AgentToolResult<ReadToolDetails?> result)
        => result.Content.OfType<TextContent>().Single().Text;

    [Fact]
    public async Task ExecuteAsync_ExtractorTextRoutesThroughOffsetAndLimit()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("papers/a.pdf", "binary\0garbage-payload");
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor((path, bytes) => new FileContentExtractionResult("line1\nline2\nline3\nline4\nline5")));
        var tool = new ReadTool(env, contentExtractors: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("papers/a.pdf", Offset: 2, Limit: 2));
        Assert.Equal("line2\nline3\n\n[2 more lines in file. Use offset=4 to continue.]", ExecuteText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ExtractorTextMatchesPlainTextFileParity()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("doc.txt", "alpha\nbeta\ngamma\ndelta");
        var plainTool = new ReadTool(env);
        var plainResult = await plainTool.ExecuteAsync("call-1", new ReadToolInput("doc.txt", Offset: 2, Limit: 2));

        env.AddFile("papers/x.pdf", "ignored-bytes");
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor((_, _) => new FileContentExtractionResult("alpha\nbeta\ngamma\ndelta")));
        var extractorTool = new ReadTool(env, contentExtractors: registry);
        var extractedResult = await extractorTool.ExecuteAsync("call-1", new ReadToolInput("papers/x.pdf", Offset: 2, Limit: 2));
    }

    [Fact]
    public async Task ExecuteAsync_ExtractorNoteIsPrependedToOutput()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("papers/a.pdf", "garbage");
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor((_, _) => new FileContentExtractionResult("hello", Note: "[Extracted text from PDF, 2 pages]")));
        var tool = new ReadTool(env, contentExtractors: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("papers/a.pdf"));

        Assert.StartsWith("[Extracted text from PDF, 2 pages]\n", ExecuteText(result));
        Assert.Contains("hello", ExecuteText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ExtractorNullFallsThroughToUtf8()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("papers/a.pdf", "hello from utf8 fallback");
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor((_, _) => null));
        var tool = new ReadTool(env, contentExtractors: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("papers/a.pdf"));

        Assert.Equal("hello from utf8 fallback", ExecuteText(result));
    }

    [Fact]
    public async Task ExecuteAsync_ExtractorExceptionFallsBackToUtf8()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("papers/a.pdf", "utf8 keeps this");
        var registry = new FileContentExtractorRegistry();
        registry.Register(new StubExtractor((_, _) => throw new InvalidDataException("encrypted pdf")));
        var tool = new ReadTool(env, contentExtractors: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("papers/a.pdf"));

        Assert.Equal("utf8 keeps this", ExecuteText(result));
    }

    [Fact]
    public async Task ExecuteAsync_FirstMatchingNonNullExtractorWins()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("papers/a.pdf", "garbage");
        var registry = new FileContentExtractorRegistry();
        var first = new StubExtractor((_, _) => new FileContentExtractionResult("first wins"), id: "first");
        var second = new StubExtractor((_, _) => new FileContentExtractionResult("second"), id: "second");
        registry.Register(first);
        registry.Register(second);
        var tool = new ReadTool(env, contentExtractors: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("papers/a.pdf"));

        Assert.Equal("first wins", ExecuteText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutRegistry_Utf8DecodeUnchanged()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("papers/a.pdf", "plain utf8 text");
        var tool = new ReadTool(env); // no registry

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("papers/a.pdf"));

        Assert.Equal("plain utf8 text", ExecuteText(result));
    }
}
