using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Cli.Tests.Bootstrap;

public sealed class ToolSelectorTests
{
    [Fact]
    public void NoToolsDisablesBuiltIns()
    {
        var selection = RuntimeToolSelector.Create(new SystemExecutionEnv(Directory.GetCurrentDirectory()), new RuntimeToolOptions(DisableAll: true));

        Assert.Empty(selection.Tools);
        Assert.Null(selection.ActiveToolNames);
    }

    [Fact]
    public void ValidatesUnknownActiveToolNamesBeforeHarnessConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => RuntimeToolSelector.Create(new SystemExecutionEnv(Directory.GetCurrentDirectory()), new RuntimeToolOptions(ActiveToolNames: ["missing"])));

        Assert.Contains("Unknown tool 'missing'", ex.Message);
    }

    [Fact]
    public void KeepsAllToolsWhileRestrictingActiveNames()
    {
        var selection = RuntimeToolSelector.Create(new SystemExecutionEnv(Directory.GetCurrentDirectory()), new RuntimeToolOptions(ActiveToolNames: ["read", "bash"]));

        Assert.NotEmpty(selection.Tools);
        Assert.Equal(["read", "bash"], selection.ActiveToolNames);
    }
}
