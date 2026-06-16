using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

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
}
