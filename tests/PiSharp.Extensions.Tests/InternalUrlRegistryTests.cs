using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class InternalUrlRegistryTests
{
    private sealed class EchoResolver(string scheme = "echo") : IInternalUrlResolver
    {
        public string Scheme { get; } = scheme;
        public ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
            => ValueTask.FromResult(new InternalUrlResult(true, $"{request.Scheme}:{request.Target}"));
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsResolver()
    {
        var registry = new InternalUrlRegistry();
        var resolver = new EchoResolver();
        registry.Register(resolver);

        Assert.True(registry.TryGet("echo", out var found));
        Assert.Same(resolver, found);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        var registry = new InternalUrlRegistry();
        registry.Register(new EchoResolver("skill"));

        Assert.True(registry.TryGet("SKILL", out var found));
        Assert.Equal("skill", found.Scheme);
    }

    [Fact]
    public void Register_DuplicateScheme_Throws()
    {
        var registry = new InternalUrlRegistry();
        registry.Register(new EchoResolver("echo"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new EchoResolver("echo")));
    }

    [Fact]
    public void Register_DuplicateSchemeWithOverride_Replaces()
    {
        var registry = new InternalUrlRegistry();
        var first = new EchoResolver("echo");
        var second = new EchoResolver("echo");
        registry.Register(first);
        registry.Register(second, overrideExisting: true);

        Assert.True(registry.TryGet("echo", out var found));
        Assert.Same(second, found);
    }

    [Fact]
    public void Unregister_RemovesScheme()
    {
        var registry = new InternalUrlRegistry();
        registry.Register(new EchoResolver("echo"));

        Assert.True(registry.Unregister("ECHO"));
        Assert.False(registry.TryGet("echo", out _));
        Assert.False(registry.Unregister("echo"));
    }

    [Fact]
    public void TryGet_UnknownScheme_ReturnsFalse()
    {
        var registry = new InternalUrlRegistry();
        Assert.False(registry.TryGet("nope", out _));
    }

    [Fact]
    public void Schemes_AreSortedAndLowerCased()
    {
        var registry = new InternalUrlRegistry();
        registry.Register(new EchoResolver("zeta"));
        registry.Register(new EchoResolver("Alpha"));
        registry.Register(new EchoResolver("mid"));

        Assert.Equal(["alpha", "mid", "zeta"], registry.Schemes);
    }

    [Fact]
    public async Task Resolver_ReceivesSchemeTargetAndQuery()
    {
        var seen = new List<InternalUrlRequest>();
        var registry = new InternalUrlRegistry();
        registry.Register(new CallbackResolver(request => { seen.Add(request); return new InternalUrlResult(true, "ok"); }));

        Assert.True(registry.TryGet("echo", out var resolver));
        var result = await resolver.ResolveAsync(new InternalUrlRequest("echo", "docs/plan.md", "limit=5", 1, 5), CancellationToken.None);

        Assert.True(result.Resolved);
        var request = Assert.Single(seen);
        Assert.Equal("docs/plan.md", request.Target);
        Assert.Equal("limit=5", request.Query);
        Assert.Equal(1, request.Offset);
        Assert.Equal(5, request.Limit);
    }

    private sealed class CallbackResolver(Func<InternalUrlRequest, InternalUrlResult> callback) : IInternalUrlResolver
    {
        public string Scheme => "echo";
        public ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
            => ValueTask.FromResult(callback(request));
    }
}
