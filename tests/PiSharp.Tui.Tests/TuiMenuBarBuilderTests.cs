using System.Linq;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Shell;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiMenuBarBuilderTests
{
    [Fact]
    public void Build_GroupsCustomEntriesUnderTheirMenu_AfterBuiltIns()
    {
        var menus = new[]
        {
            new TuiMenuEntry("View", "Toggle Left", "toggle-left", SourceId: "ext-1"),
            new TuiMenuEntry("Tools", "Run Lint", "lint", SourceId: "ext-2"),
        };

        var built = TuiMenuBarBuilder.Build(menus, _ => { });

        Assert.Contains(built, m => m.Title == "_File");
        Assert.Contains(built, m => m.Title == "_View");
        Assert.Contains(built, m => m.Title == "Tools");
        var view = built.Single(m => m.Title == "_View");
        Assert.Contains(view.Children.OfType<Terminal.Gui.MenuItem>(), c => c.Title == "Toggle Left");
    }
}
