using System.Text.Json;
using System.Threading.Tasks;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiExtensionUiMenuTests
{
    private static TuiRenderState NewState()
        => TuiRenderState.Empty("s1", null, new ModelDescriptor("prov", "id", "Model"), ThinkingLevel.Off, null);

    [Fact]
    public async Task RequestAsync_RegisterMenuItem_AddsEntryAndReturnsOk()
    {
        var state = NewState();
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state));
        host.DispatchUi = action => action();
        var ui = new TuiExtensionUi(host);

        var payload = JsonDocument.Parse(
            "{\"menu\":\"View\",\"label\":\"Toggle\",\"command\":\"toggle-left\"}").RootElement;
        var result = await ui.RequestAsync(new ExtensionUiRequest("ext-1", "register_menu_item", payload));

        Assert.True(result.Ok);
        Assert.Single(state.CustomMenus);
        Assert.Equal("toggle-left", state.CustomMenus[0].Command);
    }

    [Fact]
    public async Task RequestAsync_Widget_PreservesSidebarLeftPlacement()
    {
        var state = NewState();
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state));
        host.DispatchUi = action => action();
        var ui = new TuiExtensionUi(host);

        var payload = JsonDocument.Parse(
            "{\"message\":\"hello\",\"placement\":\"sidebar-left\"}").RootElement;
        await ui.RequestAsync(new ExtensionUiRequest("ext-1", "widget", payload));

        Assert.Single(state.BridgeSlots);
        Assert.Equal("sidebar-left", state.BridgeSlots[0].Placement);
    }
}
