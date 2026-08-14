using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Tools.Tests.Fakes;
using PiSharp.Tools.Files;
using Xunit;

namespace PiSharp.Tools.Tests;

/// <summary>
/// Verifies the P26 internal-URL routing in <see cref="ReadTool"/>: scheme
/// interception before filesystem resolution, traversal blocking, unknown
/// scheme errors, resolver dispatch, and un-resolved results.
/// </summary>
public sealed class ReadToolInternalUrlTests
{
    private sealed class StubResolver(string scheme = "skill") : IInternalUrlResolver
    {
        public string Scheme { get; } = scheme;
        public List<InternalUrlRequest> Requests { get; } = [];
        public bool Resolved { get; set; } = true;
        public string Content { get; set; } = "resolver content line 1\nline 2";
        public InternalUrlError? Error { get; set; }

        public ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Resolved ? new InternalUrlResult(true, Content) : new InternalUrlResult(false, null, Error));
        }
    }

    [Fact]
    public async Task ExecuteAsync_InternalUrl_RoutesToResolver()
    {
        var env = new FakeExecutionEnv("/repo");
        var resolver = new StubResolver();
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("skill://docs/plan.md"));

        Assert.Contains("resolver content line 1", result.Content.OfType<TextContent>().Single().Text);
        var request = Assert.Single(resolver.Requests);
        Assert.Equal("skill", request.Scheme);
        Assert.Equal("docs/plan.md", request.Target);
        Assert.Null(request.Query);
    }

    [Fact]
    public async Task ExecuteAsync_InternalUrl_PassesQueryAndOffset()
    {
        var env = new FakeExecutionEnv("/repo");
        var resolver = new StubResolver();
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        await tool.ExecuteAsync("call-1", new ReadToolInput("skill://docs/plan.md?format=raw", Offset: 2, Limit: 3));

        var request = Assert.Single(resolver.Requests);
        Assert.Equal("format=raw", request.Query);
        Assert.Equal(2, request.Offset);
        Assert.Equal(3, request.Limit);
    }

    [Fact]
    public async Task ExecuteAsync_InternalUrl_TraversalTargetIsBlockedWithoutResolverCall()
    {
        var env = new FakeExecutionEnv("/repo");
        var resolver = new StubResolver();
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("skill://../secret.txt"));

        Assert.Contains("Blocked internal URL", result.Content.OfType<TextContent>().Single().Text);
        Assert.Empty(resolver.Requests);
    }

    [Theory]
    [InlineData("skill:///absolute")]
    [InlineData("skill://a/../b")]
    [InlineData("skill://a%2f..%2fb")]
    [InlineData("skill://~home")]
    [InlineData("skill://")]
    public async Task ExecuteAsync_InternalUrl_VariousTraversalFormsAreBlocked(string path)
    {
        var env = new FakeExecutionEnv("/repo");
        var resolver = new StubResolver();
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput(path));

        Assert.Contains("Blocked internal URL", result.Content.OfType<TextContent>().Single().Text);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_InternalUrl_UnknownSchemeReportsRegisteredSchemes()
    {
        var env = new FakeExecutionEnv("/repo");
        var resolver = new StubResolver("skill");
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("nope://something"));

        var text = result.Content.OfType<TextContent>().Single().Text;
        Assert.Contains("Unknown internal URL scheme 'nope'", text);
        Assert.Contains("skill", text);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_InternalUrl_UnresolvedReturnsErrorText()
    {
        var env = new FakeExecutionEnv("/repo");
        var resolver = new StubResolver { Resolved = false, Error = new InternalUrlError(InternalUrlErrorKind.NotFound, "missing plan") };
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("skill://docs/missing.md"));

        var text = result.Content.OfType<TextContent>().Single().Text;
        Assert.Contains("NotFound", text);
        Assert.Contains("missing plan", text);
    }

    [Fact]
    public async Task ExecuteAsync_PlainPath_StillReadsFilesystem()
    {
        var env = new FakeExecutionEnv("/repo");
        env.AddFile("/repo/notes.txt", "filesystem content");
        var resolver = new StubResolver();
        var registry = new InternalUrlRegistry();
        registry.Register(resolver);
        var tool = new ReadTool(env, urlRegistry: registry);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("notes.txt"));

        Assert.Contains("filesystem content", result.Content.OfType<TextContent>().Single().Text);
        Assert.Empty(resolver.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutRegistry_InternalUrlReportsNoSchemes()
    {
        var env = new FakeExecutionEnv("/repo");
        var tool = new ReadTool(env);

        var result = await tool.ExecuteAsync("call-1", new ReadToolInput("skill://docs/plan.md"));

        var text = result.Content.OfType<TextContent>().Single().Text;
        Assert.Contains("Unknown internal URL scheme 'skill'", text);
        Assert.Contains("none", text);
    }
}
