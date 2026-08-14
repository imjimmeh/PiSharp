using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class NuGetVersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.0", false)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0-beta.1", false)]
    [InlineData("1.2.0-beta.1", "1.2.0", false)]
    [InlineData("1.2.0+abc123", "1.2.1", true)]
    public void IsOlderThan_ComparesCoreVersions(string installed, string latest, bool expected)
        => Assert.Equal(expected, NuGetVersionComparer.IsOlderThan(installed, latest));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void IsOlderThan_ReturnsFalse_WhenUnparseable(string? installed)
        => Assert.False(NuGetVersionComparer.IsOlderThan(installed, "1.0.0"));

    [Fact]
    public void IsOlderThan_ReturnsFalse_WhenLatestUnparseable()
        => Assert.False(NuGetVersionComparer.IsOlderThan("1.0.0", "bogus"));

    [Theory]
    [InlineData("1.2.3", 0)]                    // Stable
    [InlineData("1.2.3-beta.1", 1)]             // PreRelease
    [InlineData("1.2.3+abc", 0)]                // Stable (build metadata)
    [InlineData("1.2", 2)]                      // Unparseable
    [InlineData("x.y.z", 2)]                    // Unparseable
    [InlineData("", 2)]                         // Unparseable
    [InlineData(null, 2)]                       // Unparseable
    public void ParseStability_Classifies(string? version, int expectedStability)
        => Assert.Equal((VersionStability)expectedStability, NuGetVersionComparer.ParseStability(version));

    [Theory]
    [InlineData("1.2.3+abc", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void Normalize_StripsBuildMetadata(string version, string expected)
        => Assert.Equal(expected, NuGetVersionComparer.Normalize(version));
}
