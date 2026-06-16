using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Rendering;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiPanelRendererTests
{
    [Fact]
    public void RenderWrapsPanelRowsAndPreservesInteractionTarget()
    {
        var target = new TuiInteractionTarget("tool", "tool-1", Action: "toggle");

        var rows = TuiPanelRenderer.Render("Tool read", ["alpha beta gamma delta"], TuiChatRowKind.ToolSucceeded, 12, target);

        Assert.True(rows.Count > 3);
        Assert.All(rows, row =>
        {
            Assert.Equal(12, row.Text.Length);
            Assert.Same(target, row.InteractionTarget);
            Assert.DoesNotContain("…", row.Text);
        });
    }
}
