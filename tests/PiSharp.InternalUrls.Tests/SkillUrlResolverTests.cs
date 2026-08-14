using PiSharp.Agent.Resources;
using PiSharp.Extensions;
using PiSharp.InternalUrls.Resolvers;
using Xunit;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Covers <c>skill://</c> path → content mapping (§5.6): body resolution,
/// containment-checked asset reads, and the full traversal/security table
/// (§11 key coverage: every attempt yields <see cref="InternalUrlErrorKind.TraversalBlocked"/>
/// and never touches the lookup/asset layer).
/// </summary>
public sealed class SkillUrlResolverTests
{
    private const string SkillDir = @"C:\skills\web";
    private const string SkillPath = SkillDir + @"\SKILL.md";

    private static Skill NewWebSkill() => new("web", "Web skills", "body of web skill", SkillPath);

    [Theory]
    // Absolute forms, escapes, percent-encoded traversal, "..", "~", "//" (§5.5 table).
    [InlineData("/absolute")]
    [InlineData("\\absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("~home")]
    [InlineData("//double")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    [InlineData("..")]
    [InlineData("a%2fb")]
    [InlineData("a%5cb")]
    [InlineData("%2e%2e")]
    [InlineData("%252e%252e")]
    [InlineData("a%2e%2e")]
    [InlineData("")]
    public async Task ResolveAsync_TraversalAttempt_IsBlocked(string target)
    {
        var resolver = new SkillUrlResolver(_ => NewWebSkill(), new FakeExecutionEnv());

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", target, null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.TraversalBlocked, result.Error!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_TraversalAttempt_NeverInvokesSkillLookup()
    {
        var lookupCalls = 0;
        var resolver = new SkillUrlResolver(_ =>
        {
            lookupCalls++;
            return NewWebSkill();
        });

        foreach (var target in new[] { "a/../b", "..", "/abs", "~x", "a%2f..%2fb", "" })
        {
            _ = await resolver.ResolveAsync(new InternalUrlRequest("skill", target, null), CancellationToken.None);
        }

        Assert.Equal(0, lookupCalls);
    }

    [Fact]
    public async Task ResolveAsync_UnknownSkill_ReturnsNotFound()
    {
        var resolver = new SkillUrlResolver(_ => null);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", "nope", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
        Assert.Contains("nope", result.Error!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_KnownSkill_ReturnsSkillBody()
    {
        var resolver = new SkillUrlResolver(_ => NewWebSkill());

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", "web", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("body of web skill", result.Content);
    }

    [Fact]
    public async Task ResolveAsync_AssetInsideSkillDir_ReturnsFileContent()
    {
        const string asset = SkillDir + @"\assets\readme.txt";
        var env = new FakeExecutionEnv(new Dictionary<string, string> { [asset] = "asset contents" });
        var resolver = new SkillUrlResolver(_ => NewWebSkill(), env);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", "web/assets/readme.txt", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("asset contents", result.Content);
    }

    [Fact]
    public void IsContainedWithin_AcceptsDescendantsAndRejectsSiblingsAndPrefixes()
    {
        // Post-resolution containment guard: a returned filesystem path must
        // stay under the scheme-declared root (case-insensitive).
        Assert.True(InternalUrlSecurity.IsContainedWithin(SkillDir + @"\assets\readme.txt", SkillDir));
        Assert.False(InternalUrlSecurity.IsContainedWithin(@"C:\skills\other\secret.txt", SkillDir));
        Assert.False(InternalUrlSecurity.IsContainedWithin(@"C:\skills\web2\secret.txt", SkillDir));
        Assert.False(InternalUrlSecurity.IsContainedWithin(SkillPath, @"C:\skills\web\SKILL.md"));
    }

    [Fact]
    public async Task ResolveAsync_AssetNotFound_ReturnsNotFound()
    {
        var env = new FakeExecutionEnv(); // empty
        var resolver = new SkillUrlResolver(_ => NewWebSkill(), env);

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", "web/assets/missing.txt", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_AssetWithoutEnv_ReturnsForbidden()
    {
        var resolver = new SkillUrlResolver(_ => NewWebSkill());

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", "web/assets/readme.txt", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.Forbidden, result.Error!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_SkillWithoutOnDiskLocation_AssetReadIsNotFound()
    {
        var resolver = new SkillUrlResolver(_ => new Skill("bare", "Bare skill", "body", "SKILL.md"), new FakeExecutionEnv());

        var result = await resolver.ResolveAsync(new InternalUrlRequest("skill", "bare/assets/x.txt", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }
}
