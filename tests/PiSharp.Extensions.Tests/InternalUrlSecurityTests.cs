using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class InternalUrlSecurityTests
{
    [Theory]
    [InlineData("/absolute")]
    [InlineData("//double-slash")]
    [InlineData("a/../b")]
    [InlineData("a/b/..")]
    [InlineData("../up")]
    [InlineData("a\\b")]
    [InlineData("a\\..\\b")]
    [InlineData("~home")]
    [InlineData("C:/windows")]
    [InlineData("c:\\windows")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    [InlineData("a/%2e%2e/b")]
    [InlineData("a/%2f/b")]
    [InlineData("a/%5c/b")]
    [InlineData("a/%252e%252e/b")]
    [InlineData("a/%2e%2e%2f/b")]
    [InlineData("")]
    [InlineData("a\0b")]
    public void TryParseTarget_BlocksTraversalAndEscapes(string target)
    {
        Assert.False(InternalUrlSecurity.TryParseTarget(target, out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a/b")]
    [InlineData("docs/plans/2026-08-14-plugin-advisor.md")]
    [InlineData("a.b/c_d-e")]
    public void TryParseTarget_AcceptsPlainRelativeTargets(string target)
    {
        Assert.True(InternalUrlSecurity.TryParseTarget(target, out var segments));
        Assert.NotEmpty(segments);
    }

    [Fact]
    public void TryParseTarget_RemovesSingleDotSegments()
    {
        Assert.True(InternalUrlSecurity.TryParseTarget("a/./b", out var segments));
        Assert.Equal(["a", "b"], segments);
    }

    [Theory]
    [InlineData(@"C:\windows\system32", @"C:\windows")]
    [InlineData(@"C:\windows\system32\drivers", @"C:\windows")]
    [InlineData("/repo/docs/plan.md", "/repo")]
    public void IsContainedWithin_AcceptsNestedPaths(string path, string root)
    {
        Assert.True(InternalUrlSecurity.IsContainedWithin(path, root));
    }

    [Theory]
    [InlineData(@"C:\windows2\file", @"C:\windows")]
    [InlineData("/repo-other/file", "/repo")]
    public void IsContainedWithin_RejectsSiblingPrefixes(string path, string root)
    {
        Assert.False(InternalUrlSecurity.IsContainedWithin(path, root));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("SKILL.md")]
    [InlineData("2026-08-14-notes")]
    [InlineData("a_b-c.d")]
    public void IsPlainName_AcceptsPlainNames(string segment)
    {
        Assert.True(InternalUrlSecurity.IsPlainName(segment));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("")]
    public void IsPlainName_RejectsNonPlainNames(string segment)
    {
        Assert.False(InternalUrlSecurity.IsPlainName(segment));
    }
}
