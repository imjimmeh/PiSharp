using System.Text.Json;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;

namespace PiSharp.Server.UiBridge;

/// <summary>
/// Daemon-side <see cref="IExtensionUi"/>: forwards extension UI requests to the owning session's
/// <c>ui_request</c> lane via <see cref="IServerUiBridge"/>, so an attached interactive client can
/// answer permission approvals, confirmations, and custom UI. With no client attached it denies
/// immediately — no round-trip latency, no orphan <c>ui_request</c> events — keeping headless and
/// SDK-session behavior identical to the previous hard deny. Every typed <see cref="IExtensionUi"/>
/// member is a thin wrapper over the same lane, so daemon behavior is consistent with local
/// (<c>TuiExtensionUi</c>): a typed call behaves exactly like the
/// underlying <c>RequestAsync</c> kind round-trip.
/// </summary>
public sealed class DaemonExtensionUi(LiveServerSession live, IServerUiBridge bridge) : IExtensionUi
{
    public async Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
    {
        var response = await ForwardAsync(request, cancellationToken).ConfigureAwait(false);
        return response is null
            ? new ExtensionUiResult(false, Error: "No interactive client is attached to this daemon session.")
            : new ExtensionUiResult(!response.Cancelled, response.Value?.ToString(), Error: response.Cancelled ? "UI request cancelled (no client answered)." : null);
    }

    /// <summary>
    /// Shared transport for every member: gates on an attached client, maps the request to a
    /// <see cref="ServerUiIntent"/>, and awaits the bridge response under Task 9's per-kind timeout
    /// (<c>responseTimeout: null</c>). Theme kinds are answered daemon-side by the bridge.
    /// </summary>
    private async Task<ServerUiResponse?> ForwardAsync(ExtensionUiRequest request, CancellationToken cancellationToken)
    {
        if (live.AttachedClients == 0) return null;

        var message = GetString(request.Payload, "message");
        // TuiExtensionUi routes "prompt" through its InputAsync lane, whose wire kind is "input".
        var kind = request.Kind == "prompt" ? "input" : request.Kind;
        // "input"/"editor" prefills ride the intent's Message slot (mirrors TuiExtensionUi.InputAsync).
        var initialValue = kind is "input" or "editor" ? GetString(request.Payload, "initialValue") : null;
        var intent = new ServerUiIntent(
            Guid.NewGuid().ToString("N"),
            kind,
            Title: message ?? string.Empty,
            Message: initialValue ?? message,
            Options: GetOptions(request.Payload),
            Component: request.Payload,
            ExtensionId: request.ExtensionId);

        return await bridge.RequestUiAsync(intent, live, responseTimeout: null, cancellationToken).ConfigureAwait(false);
    }

    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(string.Empty, "notify", JsonSerializer.SerializeToElement(new { message })), cancellationToken);

    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ExtensionUiRequest(string.Empty, "confirm", JsonSerializer.SerializeToElement(new { message })), cancellationToken).ConfigureAwait(false);
        return result.Ok && result.Value?.ToString() is "true" or "True";
    }

    public async Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ExtensionUiRequest(string.Empty, "prompt", JsonSerializer.SerializeToElement(new { message = prompt, initialValue })), cancellationToken).ConfigureAwait(false);
        return result.Ok ? result.Value?.ToString() : null;
    }

    public async Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ExtensionUiRequest(string.Empty, "select", JsonSerializer.SerializeToElement(new { message = prompt, options })), cancellationToken).ConfigureAwait(false);
        return result.Ok ? result.Value?.ToString() : null;
    }

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "status", JsonSerializer.SerializeToElement(new { message = status })), cancellationToken);

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "widget", JsonSerializer.SerializeToElement(new { message = widget?.Content, title = widget?.Title })), cancellationToken);

    // --- remaining typed IExtensionUi members forwarded through the same lane (P2 parity) ---

    public Task SetTitleAsync(string extensionId, string? title, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "title", JsonSerializer.SerializeToElement(new { message = title, title })), cancellationToken);

    public async Task<string?> GetEditorTextAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ExtensionUiRequest(extensionId, "editor_get_text", JsonSerializer.SerializeToElement(new { })), cancellationToken).ConfigureAwait(false);
        return result.Ok ? result.Value?.ToString() : null;
    }

    public Task SetEditorTextAsync(string extensionId, string text, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "editor_set_text", JsonSerializer.SerializeToElement(new { text })), cancellationToken);

    public Task SetWorkingMessageAsync(string? message, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(string.Empty, "working_message", JsonSerializer.SerializeToElement(new { message })), cancellationToken);

    public Task SetWorkingVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(string.Empty, "working_visible", JsonSerializer.SerializeToElement(new { visible })), cancellationToken);

    public Task SetWorkingIndicatorAsync(ExtensionWorkingIndicator? indicator, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(string.Empty, "working_indicator", JsonSerializer.SerializeToElement(new { indicator = new { message = indicator?.Message, visible = indicator?.Visible, spinner = indicator?.Spinner } })), cancellationToken);

    public Task SetFooterAsync(string extensionId, ExtensionWidgetState? footer, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "footer", JsonSerializer.SerializeToElement(new { message = footer?.Content, kind = footer?.Kind, title = footer?.Title, placement = footer?.Placement })), cancellationToken);

    public Task SetHeaderAsync(string extensionId, ExtensionWidgetState? header, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "header", JsonSerializer.SerializeToElement(new { message = header?.Content, kind = header?.Kind, title = header?.Title, placement = header?.Placement })), cancellationToken);

    public Task RegisterMenuItemAsync(string extensionId, ExtensionMenuItem item, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "register_menu_item", JsonSerializer.SerializeToElement(new { menu = item.Menu, label = item.Label, command = item.Command, shortcut = item.Shortcut })), cancellationToken);

    public async Task<bool> GetToolsExpandedAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ExtensionUiRequest(string.Empty, "tools_expanded_get", JsonSerializer.SerializeToElement(new { })), cancellationToken).ConfigureAwait(false);
        return result.Ok && result.Value?.ToString() == "true";
    }

    public Task SetToolsExpandedAsync(bool expanded, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(string.Empty, "tools_expanded_set", JsonSerializer.SerializeToElement(new { expanded })), cancellationToken);

    public Task SetEditorComponentAsync(string extensionId, ExtensionWidgetState? component, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(extensionId, "editor_component_set", JsonSerializer.SerializeToElement(new { component = component?.Content, message = component?.Content, kind = component?.Kind, title = component?.Title, placement = component?.Placement })), cancellationToken);

    public async Task<ExtensionWidgetState?> GetEditorComponentAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(new ExtensionUiRequest(extensionId, "editor_component_get", JsonSerializer.SerializeToElement(new { })), cancellationToken).ConfigureAwait(false);
        // The client answers with a typed widget; the wire strings it, so recover it from JSON.
        if (!result.Ok || result.Value is not string text || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<ExtensionWidgetState>(text, ServerJsonSerializer.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Theme kinds are answered daemon-side by ServerUiBridge's registry interception, so these use
    // the bridge response directly rather than the stringified Result.
    public async Task<IReadOnlyList<ExtensionThemeInfo>> GetAllThemesAsync(CancellationToken cancellationToken = default)
    {
        var response = await ForwardAsync(new ExtensionUiRequest(string.Empty, "get_all_themes", JsonSerializer.SerializeToElement(new { })), cancellationToken).ConfigureAwait(false);
        if (response is null || response.Cancelled) return [];
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<ExtensionThemeInfo>>(
                JsonSerializer.Serialize(response.Value, ServerJsonSerializer.Options),
                ServerJsonSerializer.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<ExtensionThemeInfo?> GetThemeAsync(CancellationToken cancellationToken = default)
    {
        var response = await ForwardAsync(new ExtensionUiRequest(string.Empty, "get_theme", JsonSerializer.SerializeToElement(new { })), cancellationToken).ConfigureAwait(false);
        if (response is null || response.Cancelled) return null;
        try
        {
            return JsonSerializer.Deserialize<ExtensionThemeInfo>(
                JsonSerializer.Serialize(response.Value, ServerJsonSerializer.Options),
                ServerJsonSerializer.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetThemeAsync(string name, CancellationToken cancellationToken = default)
        => RequestAsync(new ExtensionUiRequest(string.Empty, "set_theme", JsonSerializer.SerializeToElement(new { name })), cancellationToken);

    private static string? GetString(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string>? GetOptions(JsonElement payload)
    {
        foreach (var property in new[] { "options", "choices", "items" })
        {
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;

            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
        }

        return null;
    }
}
