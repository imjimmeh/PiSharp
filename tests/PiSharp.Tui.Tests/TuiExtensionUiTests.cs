using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;
using System.Text.Json;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;

namespace PiSharp.Tui.Tests;

public sealed class TuiExtensionUiTests
{
    [Fact]
    public async Task SelectAsync_invokes_injected_delegate_and_returns_its_result()
    {
        var host = new ExtensionUiBridgeHost(new Window()) { DispatchUi = a => a() };
        var ui = new TuiExtensionUi(host, selectAsync:
            (prompt, options, _) => Task.FromResult<string?>(options.ElementAt(1)));

        var result = await ui.SelectAsync("Choose:", ["a", "b", "c"], CancellationToken.None);

        Assert.Equal("b", result); // second item — NOT first (would be "a" with the stub)
    }

    [Fact]
    public async Task SelectAsync_falls_back_to_stub_when_no_delegate_provided()
    {
        var host = new ExtensionUiBridgeHost(new Window()) { DispatchUi = a => a() };
        var ui = new TuiExtensionUi(host); // no delegate

        // stub returns options[0]
        var result = await ui.SelectAsync("Choose:", ["x", "y"], CancellationToken.None);

        Assert.Equal("x", result);
    }

    [Fact]
    public async Task Request_ToolsExpandedSet_TogglesShowToolOutput()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };
        var ui = new TuiExtensionUi(host);

        using var payload = JsonDocument.Parse("""{ "expanded": true }""");
        var result = await ui.RequestAsync(new ExtensionUiRequest("ext", "tools_expanded_set", payload.RootElement), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(state.ShowToolOutput);
    }

    [Fact]
    public async Task Request_ToolsExpandedGet_ReadsCurrentValue()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null).SetToolOutput(true);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };
        var ui = new TuiExtensionUi(host);

        using var payload = JsonDocument.Parse("{}");
        var result = await ui.RequestAsync(new ExtensionUiRequest("ext", "tools_expanded_get", payload.RootElement), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True((bool)(result.Value ?? false));
    }

    [Fact]
    public async Task Request_EditorComponentSet_StoresEditorSlot()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };
        var ui = new TuiExtensionUi(host);

        using var payload = JsonDocument.Parse("""{ "message": "component body", "title": "Ext" }""");
        var result = await ui.RequestAsync(new ExtensionUiRequest("ext", "editor_component_set", payload.RootElement), CancellationToken.None);

        Assert.True(result.Ok);
        var slot = Assert.Single(state.BridgeSlots);
        Assert.Equal("editor", slot.Placement);
        Assert.Equal("component body", slot.Content);
    }

    [Fact]
    public async Task Request_EditorComponentGet_ReturnsStoredComponent()
    {
        var state = TuiRenderState.Empty("sid", null, new ModelDescriptor("test", "model", "test"), ThinkingLevel.Off, null);
        var host = new ExtensionUiBridgeHost(new Window(), update => state = update(state), getState: () => state)
        {
            DispatchUi = action => action()
        };
        var ui = new TuiExtensionUi(host);

        using var setPayload = JsonDocument.Parse("""{ "message": "body" }""");
        await ui.RequestAsync(new ExtensionUiRequest("ext", "editor_component_set", setPayload.RootElement), CancellationToken.None);

        using var getPayload = JsonDocument.Parse("{}");
        var result = await ui.RequestAsync(new ExtensionUiRequest("ext", "editor_component_get", getPayload.RootElement), CancellationToken.None);

        Assert.True(result.Ok);
        var component = Assert.IsType<ExtensionWidgetState>(result.Value);
        Assert.Equal("body", component.Content);
        Assert.Equal("editor", component.Placement);
    }

    [Fact]
    public async Task Request_EditorPaste_IsUnsupported()
    {
        var host = new ExtensionUiBridgeHost(new Window()) { DispatchUi = action => action() };
        var ui = new TuiExtensionUi(host);

        using var payload = JsonDocument.Parse("{}");
        var result = await ui.RequestAsync(new ExtensionUiRequest("ext", "editor_paste", payload.RootElement), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Unsupported extension UI request kind", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_ThemeKinds_ReturnInertResults()
    {
        var host = new ExtensionUiBridgeHost(new Window()) { DispatchUi = action => action() };
        var ui = new TuiExtensionUi(host);

        using var payload = JsonDocument.Parse("{}");
        var themes = await ui.RequestAsync(new ExtensionUiRequest("ext", "get_all_themes", payload.RootElement), CancellationToken.None);
        var theme = await ui.RequestAsync(new ExtensionUiRequest("ext", "get_theme", payload.RootElement), CancellationToken.None);
        var set = await ui.RequestAsync(new ExtensionUiRequest("ext", "set_theme", payload.RootElement), CancellationToken.None);

        Assert.True(themes.Ok);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<object>>(themes.Value));
        Assert.True(theme.Ok);
        Assert.Null(theme.Value);
        Assert.True(set.Ok);
        Assert.Null(set.Value);
    }
}
