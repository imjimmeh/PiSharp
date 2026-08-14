using System.Text.Json;
using System.Threading.Tasks;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class ExtensionThemeApiTests
{
    [Fact]
    public async Task DefaultIExtensionUi_GetAllThemes_ReturnsEmpty()
    {
        IExtensionUi ui = new DefaultUi();
        var result = await ui.GetAllThemesAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task DefaultIExtensionUi_GetTheme_ReturnsNull()
    {
        IExtensionUi ui = new DefaultUi();
        var result = await ui.GetThemeAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task DefaultIExtensionUi_SetTheme_IsNoOp()
    {
        IExtensionUi ui = new DefaultUi();
        await ui.SetThemeAsync("Light");
    }

    [Fact]
    public async Task DefaultIExtensionUi_GetToolsExpanded_ReturnsFalse()
    {
        IExtensionUi ui = new DefaultUi();
        var result = await ui.GetToolsExpandedAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task DefaultIExtensionUi_SetToolsExpanded_IsNoOp()
    {
        IExtensionUi ui = new DefaultUi();
        await ui.SetToolsExpandedAsync(true);
    }

    [Fact]
    public async Task DefaultIExtensionUi_SetEditorComponent_IsNoOp()
    {
        IExtensionUi ui = new DefaultUi();
        await ui.SetEditorComponentAsync("ext1", null);
    }

    [Fact]
    public async Task DefaultIExtensionUi_GetEditorComponent_ReturnsNull()
    {
        IExtensionUi ui = new DefaultUi();
        var result = await ui.GetEditorComponentAsync("ext1");
        Assert.Null(result);
    }

    [Fact]
    public async Task NoExtensionUi_GetAllThemes_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.GetAllThemesAsync());
    }

    [Fact]
    public async Task NoExtensionUi_GetTheme_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.GetThemeAsync());
    }

    [Fact]
    public async Task NoExtensionUi_SetTheme_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.SetThemeAsync("Light"));
    }

    [Fact]
    public async Task NoExtensionUi_GetToolsExpanded_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.GetToolsExpandedAsync());
    }

    [Fact]
    public async Task NoExtensionUi_SetToolsExpanded_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.SetToolsExpandedAsync(true));
    }

    [Fact]
    public async Task NoExtensionUi_SetEditorComponent_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.SetEditorComponentAsync("ext1", null));
    }

    [Fact]
    public async Task NoExtensionUi_GetEditorComponent_Throws()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        await Assert.ThrowsAsync<NotSupportedException>(() => ui.GetEditorComponentAsync("ext1"));
    }

    [Fact]
    public void ExtensionThemeDocument_JsonRoundTrip_DeserializesCorrectly()
    {
        var original = new ExtensionThemeDocument(
            "dark",
            new Dictionary<string, string> { ["bg"] = "#1a1a2e" },
            new ExtensionThemeColorScheme(NormalForeground: "#e0e0e0", NormalBackground: "#1a1a2e"),
            null,
            new ExtensionThemeColorScheme(FocusForeground: "#ffffff", FocusBackground: "#16213e"));

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deserialized = JsonSerializer.Deserialize<ExtensionThemeDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(deserialized);
        Assert.Equal("dark", deserialized!.Name);
        Assert.NotNull(deserialized.Tokens);
        Assert.Equal("#1a1a2e", deserialized.Tokens!["bg"]);
        Assert.NotNull(deserialized.Default);
        Assert.Equal("#e0e0e0", deserialized.Default!.NormalForeground);
        Assert.Null(deserialized.Dialog);
        Assert.NotNull(deserialized.Menu);
        Assert.Equal("#ffffff", deserialized.Menu!.FocusForeground);
    }

    [Fact]
    public void ExtensionThemeInfo_WithDocument_RoundTrips()
    {
        var info = new ExtensionThemeInfo("light",
            new ExtensionThemeDocument("light", null, new ExtensionThemeColorScheme(NormalForeground: "#000")));
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deserialized = JsonSerializer.Deserialize<ExtensionThemeInfo>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(deserialized);
        Assert.Equal("light", deserialized!.Name);
        Assert.NotNull(deserialized.Document);
        Assert.Equal("light", deserialized.Document!.Name);
    }

    [Fact]
    public void ExtensionThemeInfo_WithoutDocument_RoundTrips()
    {
        var info = new ExtensionThemeInfo("dark");
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deserialized = JsonSerializer.Deserialize<ExtensionThemeInfo>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(deserialized);
        Assert.Equal("dark", deserialized!.Name);
        Assert.Null(deserialized.Document);
    }

    /// <summary>
    /// Minimal IExtensionUi that only uses default interface implementations.
    /// </summary>
    private sealed class DefaultUi : IExtensionUi
    {
        public Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<ExtensionUiResult>(new NotSupportedException());
        public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
        public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
