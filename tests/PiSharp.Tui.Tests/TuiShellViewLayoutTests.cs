using PiSharp.Tui.Interactive.Shell;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiShellViewLayoutTests
{
    [Fact]
    public void Construction_AddsMenuBarAndSidebars()
    {
        var shell = new TuiShellView();

        Assert.NotNull(shell.MenuBar);
        Assert.NotNull(shell.LeftSidebar);
        Assert.NotNull(shell.RightSidebar);
    }

    [Fact]
    public void SetSidebarVisibility_TogglesVisibleFlag()
    {
        var shell = new TuiShellView();

        shell.SetSidebarVisibility(leftVisible: false, rightVisible: true);

        Assert.False(shell.LeftSidebar.Visible);
        Assert.True(shell.RightSidebar.Visible);
    }

    [Fact]
    public void RightSidebar_X_IsPercentBased_SoItAppearsOnScreen()
    {
        var shell = new TuiShellView();

        // Pos.AnchorEnd(0) places the view's left edge at the parent's right boundary (off-screen).
        // The correct position is Pos.Percent(100 - SidebarWidthPercent) which anchors the left
        // edge at 75% of the window width so the sidebar occupies the rightmost 25%.
        var xTypeName = shell.RightSidebar.X.GetType().Name;
        Assert.DoesNotContain("AnchorEnd", xTypeName, StringComparison.OrdinalIgnoreCase);
    }
}
