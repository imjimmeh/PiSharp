using System.Threading.Tasks;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class ExtensionUiBridgeHostMenuTests
{
    private static TuiRenderState NewState()
        => TuiRenderState.Empty("s1", null, new ModelDescriptor("prov", "id", "api", "Model"), ThinkingLevel.Off, null);

    [Fact]
    public async Task RegisterMenuItemAsync_AddsEntryWithSource()
    {
        var state = NewState();
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action() // run inline, no Terminal.Gui loop
        };

        await host.RegisterMenuItemAsync("ext-1", new ExtensionMenuItem("View", "Toggle", "toggle-left"));

        Assert.Single(state.CustomMenus);
        Assert.Equal("ext-1", state.CustomMenus[0].SourceId);
        Assert.Equal("toggle-left", state.CustomMenus[0].Command);
    }

    [Fact]
    public async Task ClearSourceAsync_RemovesMenuEntriesForSource()
    {
        var state = NewState().AddCustomMenuEntry(new TuiMenuEntry("View", "A", "a", SourceId: "ext-1"));
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state))
        {
            DispatchUi = action => action()
        };

        await host.ClearSourceAsync("ext-1");

        Assert.Empty(state.CustomMenus);
    }
}
