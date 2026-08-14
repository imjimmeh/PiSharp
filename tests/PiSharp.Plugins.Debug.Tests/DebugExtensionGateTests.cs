using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions.Testing;
using Xunit;

namespace PiSharp.Plugins.Debug.Tests;

/// <summary>
/// DebugExtension-level gating: the <c>debug</c> tool fails fast with an enable hint when
/// the <c>extensions.pisharp-debug.enabled</c> gate is off (the default). The full session
/// state machine, adapter lifecycle, and config parsing are covered in the shared Lsp.Tests
/// project; this fills the missing gate-off surface by hosting the extension on
/// <see cref="FakeExtensionApi"/>.
/// </summary>
public sealed class DebugExtensionGateTests
{
    [Fact]
    public async Task DebugToolFailsFastWhenGateIsOffByDefault()
    {
        var api = new FakeExtensionApi();
        await using var extension = new DebugExtension();
        await extension.InitializeAsync(api, CancellationToken.None);

        var tool = Assert.Single(api.RegisteredTools, t => t.Name == "debug");
        var parameters = JsonSerializer.SerializeToElement(new
        {
            op = "attach",
            language = "python",
        });

        var result = await tool.ExecuteAsync("call-1", parameters, CancellationToken.None, onUpdate: null);

        var text = Assert.Single(result.Content.OfType<TextContent>());
        Assert.Contains("disabled", text.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pisharp-debug.enabled", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DebugToolFailsFastWhenGateExplicitlySetFalse()
    {
        var api = new FakeExtensionApi();
        await api.Settings.SetAsync("enabled", false);
        await using var extension = new DebugExtension();
        await extension.InitializeAsync(api, CancellationToken.None);

        var tool = Assert.Single(api.RegisteredTools, t => t.Name == "debug");
        var result = await tool.ExecuteAsync(
            "call-1",
            JsonSerializer.SerializeToElement(new { op = "list" }),
            CancellationToken.None,
            onUpdate: null);

        var text = Assert.Single(result.Content.OfType<TextContent>());
        Assert.Contains("disabled", text.Text, StringComparison.OrdinalIgnoreCase);
    }
}
