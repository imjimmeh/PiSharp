using Xunit;

namespace PiSharp.Mcp.Tests;

public sealed class McpServerConfigTests
{
    [Theory]
    [InlineData("fileserver", true)]
    [InlineData("a", true)]
    [InlineData("a-b", true)]
    [InlineData("a1", true)]
    [InlineData("-bad", false)]
    [InlineData("bad-", false)]
    [InlineData("BadCase", false)]
    [InlineData("with space", false)]
    [InlineData("", true)]
    public void NormalizeName_ValidatesPerSpec(string name, bool valid)
    {
        var normalized = McpServerConfig.NormalizeName(name);
        Assert.Equal(valid, normalized == name);
    }

    [Fact]
    public void NormalizeName_FallsBackToEmptyForUnusableInput()
    {
        Assert.Equal(string.Empty, McpServerConfig.NormalizeName("!@#$"));
        Assert.Equal("", McpServerConfig.NormalizeName(" "));
    }

    [Fact]
    public void Validate_StdioServerRequiresCommand()
    {
        var config = TestMcp.StdioServer() with { Command = null };
        Assert.False(config.Validate(out var error));
        Assert.Contains("command", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_HttpServerRequiresUrl()
    {
        var config = TestMcp.HttpServer() with { Url = null };
        Assert.False(config.Validate(out var error));
        Assert.Contains("url", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_HttpServerRejectsRelativeUrl()
    {
        var config = TestMcp.HttpServer("/relative") ;
        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_EnabledStdioServerPasses()
    {
        var config = TestMcp.StdioServer();
        Assert.True(config.Validate(out var error), error);
    }

    [Fact]
    public void DisabledServerAlwaysValidates()
    {
        var config = TestMcp.StdioServer() with { Command = null, Enabled = false };
        Assert.True(config.Validate(out _));
    }
}
