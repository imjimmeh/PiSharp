using PiSharp.Browser.Runtime;
using Xunit;

namespace PiSharp.Browser.Tests;

public class BrowserOptionsTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void ParseEnabled_InterpretsEnvValues(string raw, bool expected)
    {
        Assert.Equal(expected, BrowserOptions.ParseEnabled(raw));
    }

    [Fact]
    public void ParseEnabled_Null_IsDisabled()
    {
        Assert.False(BrowserOptions.ParseEnabled(null));
    }

    [Fact]
    public void Resolve_Enabled_WhenEnvTrue()
    {
        var options = BrowserOptions.Resolve(name => name == "PISHARP_BROWSER_ENABLED" ? "true" : null);
        Assert.True(options.Enabled);
        Assert.Equal(30000, options.Tool.DefaultNavigationTimeoutMs);
        Assert.True(options.Tool.Headless);
    }

    [Fact]
    public void Resolve_Disabled_ByDefault()
    {
        var options = BrowserOptions.Resolve(_ => null);
        Assert.False(options.Enabled);
    }

    [Fact]
    public void Resolve_Default_ReadsProcessEnvironment()
    {
        // No custom env provider => uses Environment.GetEnvironmentVariable("PISHARP_BROWSER_ENABLED").
        // The value is not "true"/"1"/"yes" in the test process, so the gate defaults to off and the
        // plugin stays hermetic (no browser is registered or launched).
        var options = BrowserOptions.Resolve();
        Assert.False(options.Enabled);
    }
}
