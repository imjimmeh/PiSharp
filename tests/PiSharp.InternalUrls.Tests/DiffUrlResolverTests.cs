using PiSharp.Extensions;
using PiSharp.InternalUrls.Resolvers;
using PiSharp.InternalUrls.Services;
using Xunit;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Covers <c>diff://</c> (most recent edit diff) and <c>diff://&lt;path&gt;</c>
/// (per-file diff) from the in-memory <see cref="DiffLedger"/> (§5.6/§5.7).
/// Per §5.5 the central guard blocks absolute forms (drive letters included),
/// so path targets are relative and normalized through the injected normalizer,
/// exactly like <c>PathUtilities.ResolvePathAsync</c> in production.
/// </summary>
public sealed class DiffUrlResolverTests
{
    private static DiffUrlResolver CreateResolver(DiffLedger ledger)
        => new(ledger, target => target.StartsWith("src/", StringComparison.Ordinal) ? @"C:\repo\" + target[4..] : null);

    [Fact]
    public async Task ResolveAsync_NoDiffs_ReturnsNotFound()
    {
        var resolver = CreateResolver(new DiffLedger());

        var result = await resolver.ResolveAsync(new InternalUrlRequest("diff", "", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_LatestDiff_ReturnsMostRecent()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-old\n+new");
        ledger.Record(@"C:\repo\b.cs", "--- a/b.cs\n+++ b/b.cs\n@@ -1 +1 @@\n-x\n+y");
        var resolver = CreateResolver(ledger);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("diff", "", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains(@"C:\repo\b.cs", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+y", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RelativePath_ReturnsThatFilesDiff()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff-for-a");
        ledger.Record(@"C:\repo\b.cs", "diff-for-b");
        var resolver = CreateResolver(ledger);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("diff", "src/a.cs", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains("diff-for-a", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("diff-for-b", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_PathNotRecorded_ReturnsNotFound()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff-for-a");
        var resolver = CreateResolver(ledger);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("diff", "src/nope.cs", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    [InlineData("~home")]
    [InlineData("C:/repo/a.cs")]
    public async Task ResolveAsync_HostilePathTarget_IsBlocked(string target)
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff");
        var resolver = CreateResolver(ledger);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("diff", target, null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.TraversalBlocked, result.Error!.Kind);
    }
}
