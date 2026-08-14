using PiSharp.Packages;
using PiSharp.Cli.Runtime;
using Xunit;

namespace PiSharp.Cli.Tests.Runtime;

public class OutdatedPackagesSummaryTests
{
    [Fact]
    public void Format_ReturnsNull_WhenListIsEmpty()
    {
        var result = OutdatedPackagesSummary.Format([]);
        Assert.Null(result);
    }

    [Fact]
    public void Format_SinglePackage_ReturnsExpectedMessage()
    {
        var outdated = new[] { new OutdatedPackageInfo("my-plugin", "1.0.0", "2.0.0") };
        var result = OutdatedPackagesSummary.Format(outdated);
        Assert.Equal("Outdated extensions: my-plugin (1.0.0 → 2.0.0). Run `pi update` to upgrade.", result);
    }

    [Fact]
    public void Format_MultiplePackages_JoinsWithComma()
    {
        var outdated = new[]
        {
            new OutdatedPackageInfo("plugin-a", "1.0.0", "1.1.0"),
            new OutdatedPackageInfo("plugin-b", "2.0.0", "3.0.0")
        };
        var result = OutdatedPackagesSummary.Format(outdated);
        Assert.Equal(
            "Outdated extensions: plugin-a (1.0.0 → 1.1.0), plugin-b (2.0.0 → 3.0.0). Run `pi update` to upgrade.",
            result);
    }
}
