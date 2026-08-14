using PiSharp.Tui.Interactive.Shell;
using PiSharp.Tui.Interactive;
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

    [Fact]
    public void SetEditorComponentSlot_ShowsEditorAndHidesPrompt()
    {
        var shell = new TuiShellView();
        var slot = new TuiBridgeSlot("editor:ext-a", "text", "Ext", "editor body", Placement: "editor");

        shell.SetEditorComponentSlot(slot);

        Assert.True(shell.HasActiveEditorComponent);
        Assert.True(shell.EditorComponent.Visible);
        Assert.False(shell.Prompt.Visible);
        Assert.False(shell.PromptTitle.Visible);
        Assert.False(shell.PromptBottomBorder.Visible);
        Assert.Equal("editor body", shell.EditorComponent.Text?.ToString());
    }

    [Fact]
    public void SetEditorComponentSlot_NullRestoresPrompt()
    {
        var shell = new TuiShellView();
        shell.SetEditorComponentSlot(new TuiBridgeSlot("editor:ext-a", "text", "Ext", "body", Placement: "editor"));

        shell.SetEditorComponentSlot(null);

        Assert.False(shell.HasActiveEditorComponent);
        Assert.False(shell.EditorComponent.Visible);
        Assert.True(shell.Prompt.Visible);
        Assert.True(shell.PromptTitle.Visible);
        Assert.True(shell.PromptBottomBorder.Visible);
    }
}
