using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class PackageSourceParserTests
{
    [Fact]
    public void ParsesNpmScopedPackage()
    {
        var source = PiPackageSourceParser.Parse("npm:@foo/bar");

        Assert.Equal(PiPackageSourceKind.Npm, source.Kind);
        Assert.Equal("npm:@foo/bar", source.Original);
        Assert.Equal("@foo/bar", source.Identity);
        Assert.Equal("@foo/bar", source.Name);
        Assert.Null(source.VersionOrRef);
        Assert.Null(source.Host);
        Assert.Null(source.RepositoryPath);
        Assert.False(source.IsPinned);
    }

    [Fact]
    public void ParsesNpmScopedPackageWithVersion()
    {
        var source = PiPackageSourceParser.Parse("npm:@foo/bar@1.2.3");

        Assert.Equal(PiPackageSourceKind.Npm, source.Kind);
        Assert.Equal("npm:@foo/bar@1.2.3", source.Original);
        Assert.Equal("@foo/bar", source.Identity);
        Assert.Equal("@foo/bar", source.Name);
        Assert.Equal("1.2.3", source.VersionOrRef);
        Assert.True(source.IsPinned);
    }

    [Fact]
    public void ParsesGitWithHttpsUrl()
    {
        var source = PiPackageSourceParser.Parse("git:https://github.com/user/repo");

        Assert.Equal(PiPackageSourceKind.Git, source.Kind);
        Assert.Equal("git:https://github.com/user/repo", source.Original);
        Assert.Equal("github.com/user/repo", source.Identity);
        Assert.Equal("user/repo", source.Name);
        Assert.Equal("github.com", source.Host);
        Assert.Equal("user/repo", source.RepositoryPath);
    }

    [Fact]
    public void ParsesGitWithScpSyntax()
    {
        var source = PiPackageSourceParser.Parse("git:git@github.com:user/repo");

        Assert.Equal(PiPackageSourceKind.Git, source.Kind);
        Assert.Equal("github.com/user/repo", source.Identity);
        Assert.Equal("github.com", source.Host);
        Assert.Equal("user/repo", source.RepositoryPath);
    }

    [Fact]
    public void ParsesHttpUrl()
    {
        var source = PiPackageSourceParser.Parse("https://github.com/user/repo");

        Assert.Equal(PiPackageSourceKind.Git, source.Kind);
        Assert.Equal("github.com/user/repo", source.Identity);
    }

    [Fact]
    public void ParsesSshUrl()
    {
        var source = PiPackageSourceParser.Parse("ssh://git@github.com/user/repo");

        Assert.Equal(PiPackageSourceKind.Git, source.Kind);
        Assert.Equal("github.com/user/repo", source.Identity);
    }

    [Fact]
    public void ParsesLocalPath()
    {
        var source = PiPackageSourceParser.Parse("./local/path");

        Assert.Equal(PiPackageSourceKind.Local, source.Kind);
        Assert.Equal("./local/path", source.Original);
        Assert.Equal("./local/path", source.Identity);
    }

    [Fact]
    public void ScpLikeGitWithoutGitPrefixIsLocal()
    {
        var source = PiPackageSourceParser.Parse("git@github.com:user/repo");

        Assert.Equal(PiPackageSourceKind.Local, source.Kind);
    }

    [Fact]
    public void BareGithubDotComWithoutGitPrefixIsLocal()
    {
        var source = PiPackageSourceParser.Parse("github.com/user/repo");

        Assert.Equal(PiPackageSourceKind.Local, source.Kind);
    }

    [Fact]
    public void StripsNpmVersionFromIdentity()
    {
        var source = PiPackageSourceParser.Parse("npm:@foo/bar@1.2.3");

        Assert.Equal("@foo/bar", source.Identity);
    }

    [Fact]
    public void StripsGitRefFromIdentity()
    {
        var source = PiPackageSourceParser.Parse("git:https://github.com/user/repo#main");

        Assert.Equal("github.com/user/repo", source.Identity);
        Assert.Equal("main", source.VersionOrRef);
    }

    [Fact]
    public void PinnedNpmSourceIsPinned()
    {
        var source = PiPackageSourceParser.Parse("npm:@foo/bar@1.2.3");

        Assert.True(source.IsPinned);
    }

    [Fact]
    public void UnpinnedNpmSourceIsNotPinned()
    {
        var source = PiPackageSourceParser.Parse("npm:@foo/bar");

        Assert.False(source.IsPinned);
    }

    [Fact]
    public void PinnedGitSourceIsPinned()
    {
        var source = PiPackageSourceParser.Parse("git:https://github.com/user/repo#abc123");

        Assert.True(source.IsPinned);
    }
}
