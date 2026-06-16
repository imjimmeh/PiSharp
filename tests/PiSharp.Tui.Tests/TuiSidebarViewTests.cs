using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiSidebarViewTests
{
    private static TuiRenderState StateWithSlots()
        => TuiRenderState.Empty("s1", null, new ModelDescriptor("prov", "id", "Model"), ThinkingLevel.Off, null)
            .UpsertBridgeSlot(new TuiBridgeSlot("w:b", "k", "B", "right-content", Placement: "sidebar-right"))
            .UpsertBridgeSlot(new TuiBridgeSlot("w:a", "k", "A", "left-content", Placement: "sidebar-left"))
            .UpsertBridgeSlot(new TuiBridgeSlot("w:c", "k", "C", "footer-content", Placement: "footer"));

    [Fact]
    public void SlotsForSide_Left_ReturnsOnlyVisibleLeftSlots_OrderedById()
    {
        var slots = TuiSidebarView.SlotsForSide(StateWithSlots(), TuiSidebarSide.Left);

        Assert.Single(slots);
        Assert.Equal("w:a", slots[0].Id);
    }

    [Fact]
    public void SlotsForSide_Right_ReturnsOnlyVisibleRightSlots()
    {
        var slots = TuiSidebarView.SlotsForSide(StateWithSlots(), TuiSidebarSide.Right);

        Assert.Single(slots);
        Assert.Equal("w:b", slots[0].Id);
    }

    [Fact]
    public void InsertSelectedFile_InvokesCallbackWithRelativePath()
    {
        string? inserted = null;
        var view = new TuiSidebarView(TuiSidebarSide.Left, path => inserted = path);
        view.SetModifiedFiles(new[] { "src/A.cs", "src/B.cs" });

        view.InsertFileAt(1);

        Assert.Equal("src/B.cs", inserted);
    }
}
