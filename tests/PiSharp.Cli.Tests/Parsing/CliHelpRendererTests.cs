using PiSharp.Cli;
using PiSharp.Cli.Parsing;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Cli.Tests.Parsing;

public sealed class CliHelpRendererTests
{
    [Fact]
    public void RenderWithoutExtensionFlagsReturnsBaseHelpText()
    {
        var rendered = CliHelpRenderer.Render();

        Assert.Equal(CliHelpRenderer.Text, rendered);
        Assert.Contains("Usage: pisharp [options] [prompt]", rendered);
    }

    [Fact]
    public void RenderIncludesExtensionFlagsWhenPresent()
    {
        var rendered = CliHelpRenderer.Render(new CliHelpOptions([
            new ExtensionFlagRegistration("safe-mode", "Enable safe behavior", ExtensionFlagType.Boolean),
            new ExtensionFlagRegistration("profile", "Select profile", ExtensionFlagType.String)
        ]));

        Assert.Contains("Extension CLI Flags:", rendered);
        Assert.Contains("--safe-mode", rendered);
        Assert.Contains("Enable safe behavior", rendered);
        Assert.Contains("--profile <value>", rendered);
        Assert.Contains("Select profile", rendered);
    }
}
