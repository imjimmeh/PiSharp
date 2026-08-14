using PiSharp.Client;
using Xunit;

namespace PiSharp.Client.Tests;

public sealed class DaemonDiscoveryTests
{
    [Fact]
    public void IsRuntimeCompatible_MatchingMajorMinor_ReturnsTrue()
    {
        var current = $"{Environment.Version.Major}.{Environment.Version.Minor}";
        Assert.True(DaemonDiscovery.IsRuntimeCompatible(current));
    }

    [Theory]
    [InlineData("0.0.0-dev")]
    [InlineData("999.0")]
    [InlineData("garbage")]
    public void IsRuntimeCompatible_IncompatibleVersion_ReturnsFalse(string version)
    {
        Assert.False(DaemonDiscovery.IsRuntimeCompatible(version));
    }
}
