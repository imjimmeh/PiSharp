using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public class NpmVersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]   // patch bump
    [InlineData("1.0.0", "1.1.0", true)]   // minor bump
    [InlineData("1.0.0", "2.0.0", true)]   // major bump
    [InlineData("1.0.0", "1.0.0", false)]  // equal
    [InlineData("1.1.0", "1.0.9", false)]  // installed newer
    [InlineData("2.0.0", "1.9.9", false)]  // installed newer major
    public void IsOlderThan_ComparesNumerically(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, NpmVersionComparer.IsOlderThan(installed, latest));
    }

    [Theory]
    [InlineData("1.0.0-beta.1", "1.0.0", true)]  // pre-release stripped, same core → older
    [InlineData("1.0.1-alpha", "1.0.0", false)]   // pre-release stripped, installed core newer
    public void IsOlderThan_StripsPreReleaseSuffix(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, NpmVersionComparer.IsOlderThan(installed, latest));
    }

    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", null)]
    [InlineData(null, null)]
    [InlineData("", "1.0.0")]
    [InlineData("not-a-version", "1.0.0")]
    public void IsOlderThan_ReturnsFalseForInvalidInput(string? installed, string? latest)
    {
        Assert.False(NpmVersionComparer.IsOlderThan(installed, latest));
    }
}
