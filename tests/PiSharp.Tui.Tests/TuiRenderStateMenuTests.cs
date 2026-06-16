using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Tui.Interactive;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiRenderStateMenuTests
{
    private static TuiRenderState NewState()
        => TuiRenderState.Empty("s1", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);

    [Fact]
    public void AddCustomMenuEntry_AppendsImmutably()
    {
        var state = NewState();
        var entry = new TuiMenuEntry("View", "Toggle Left", "toggle-left", SourceId: "ext-1");

        var updated = state.AddCustomMenuEntry(entry);

        Assert.Empty(state.CustomMenus);
        Assert.Single(updated.CustomMenus);
        Assert.Equal(entry, updated.CustomMenus[0]);
    }

    [Fact]
    public void RemoveCustomMenuEntriesBySource_RemovesOnlyMatchingSource()
    {
        var state = NewState()
            .AddCustomMenuEntry(new TuiMenuEntry("View", "A", "a", SourceId: "ext-1"))
            .AddCustomMenuEntry(new TuiMenuEntry("View", "B", "b", SourceId: "ext-2"));

        var updated = state.RemoveCustomMenuEntriesBySource("ext-1");

        Assert.Single(updated.CustomMenus);
        Assert.Equal("ext-2", updated.CustomMenus[0].SourceId);
    }

    [Fact]
    public void ToggleSidebarVisibility_FlipsIndependentFlags()
    {
        var state = NewState();
        Assert.True(state.LeftSidebarVisible);
        Assert.True(state.RightSidebarVisible);

        var left = state.ToggleLeftSidebar();
        Assert.False(left.LeftSidebarVisible);
        Assert.True(left.RightSidebarVisible);

        var right = state.ToggleRightSidebar();
        Assert.True(right.LeftSidebarVisible);
        Assert.False(right.RightSidebarVisible);
    }
}
