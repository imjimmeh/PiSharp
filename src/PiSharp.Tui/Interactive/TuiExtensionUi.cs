using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.Tui.Interactive;

public sealed class TuiExtensionUi(ExtensionUiBridgeHost bridge, Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? selectAsync = null) : IExtensionUi
{
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? _selectAsync = selectAsync;

    public async Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
    {
        var message = GetString(request.Payload, "message");
        switch (request.Kind)
        {
            case "notify":
                if (!string.IsNullOrEmpty(message)) await NotifyAsync(message, cancellationToken: cancellationToken);
                return new ExtensionUiResult(true);
            case "select":
                return new ExtensionUiResult(true, await SelectAsync(message ?? string.Empty, GetOptions(request.Payload), cancellationToken));
            case "prompt":
                return new ExtensionUiResult(true, await InputAsync(message ?? string.Empty, GetString(request.Payload, "initialValue"), cancellationToken));
            case "editor":
                return new ExtensionUiResult(true, await OpenEditorAsync(message ?? string.Empty, GetString(request.Payload, "initialValue"), cancellationToken));
            case "confirm":
                return new ExtensionUiResult(true, await ConfirmAsync(message ?? string.Empty, cancellationToken));
            case "markdown":
                await NotifyAsync(GetString(request.Payload, "markdown") ?? message ?? string.Empty, cancellationToken: cancellationToken);
                return new ExtensionUiResult(true);
            case "editor_get_text":
                return new ExtensionUiResult(true, await GetEditorTextAsync(request.ExtensionId, cancellationToken));
            case "editor_set_text":
                await SetEditorTextAsync(request.ExtensionId, GetString(request.Payload, "text") ?? string.Empty, cancellationToken);
                return new ExtensionUiResult(true);
            case "working_message":
                await SetWorkingMessageAsync(message, cancellationToken);
                return new ExtensionUiResult(true);
            case "working_visible":
                await SetWorkingVisibleAsync(GetBoolean(request.Payload, "visible"), cancellationToken);
                return new ExtensionUiResult(true);
            case "working_indicator":
                await SetWorkingIndicatorAsync(CreateWorkingIndicator(request.Payload), cancellationToken);
                return new ExtensionUiResult(true);
            case "status":
                await SetStatusAsync(request.ExtensionId, message, cancellationToken);
                return new ExtensionUiResult(true);
            case "widget":
                await SetWidgetAsync(request.ExtensionId, CreateWidget(request.Payload), cancellationToken);
                return new ExtensionUiResult(true);
            case "footer":
                await SetFooterAsync(request.ExtensionId, CreateWidget(request.Payload, "footer"), cancellationToken);
                return new ExtensionUiResult(true);
            case "header":
                await SetHeaderAsync(request.ExtensionId, CreateWidget(request.Payload, "header"), cancellationToken);
                return new ExtensionUiResult(true);
            case "title":
                await SetTitleAsync(request.ExtensionId, GetString(request.Payload, "title") ?? message, cancellationToken);
                return new ExtensionUiResult(true);
            case "custom":
                if (GetString(request.Payload, "mode") == "interactive-component")
                    return await bridge.ShowCustomComponentAsync(request.ExtensionId, request.Payload, cancellationToken);

                return new ExtensionUiResult(false, Error: $"Unsupported custom extension UI request mode '{GetString(request.Payload, "mode") ?? "<missing>"}'.");
            case "custom_update":
                return await bridge.UpdateCustomComponentAsync(request.ExtensionId, request.Payload, cancellationToken);
            case "register_menu_item":
                await RegisterMenuItemAsync(request.ExtensionId, new ExtensionMenuItem(
                    GetString(request.Payload, "menu") ?? "Extensions",
                    GetString(request.Payload, "label") ?? request.ExtensionId,
                    GetString(request.Payload, "command") ?? string.Empty,
                    GetString(request.Payload, "shortcut")), cancellationToken);
                return new ExtensionUiResult(true);
            default:
                return new ExtensionUiResult(false, Error: $"Unsupported extension UI request kind '{request.Kind}'.");
        }
    }

    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
        => bridge.NotifyAsync(message, cancellationToken);

    private static ExtensionWidgetState? CreateWidget(JsonElement payload, string defaultKind = "widget")
    {
        var content = GetString(payload, "message");
        if (content is null) return null;
        return new ExtensionWidgetState(
            GetString(payload, "kind") ?? defaultKind,
            content,
            GetString(payload, "title"),
            NormalizePlacement(GetString(payload, "placement")));
    }

    private static string? GetString(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value)
           && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : false;

    private static bool? GetNullableBoolean(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value)
           && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> GetOptions(JsonElement payload)
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

        return [];
    }

    private static ExtensionWorkingIndicator? CreateWorkingIndicator(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("indicator", out var indicator) || indicator.ValueKind != JsonValueKind.Object)
            return null;

        return new ExtensionWorkingIndicator(
            GetString(indicator, "message"),
            GetNullableBoolean(indicator, "visible"),
            GetString(indicator, "spinner"));
    }

    private static string NormalizePlacement(string? placement)
        => placement switch
        {
            "aboveEditor" => "above-editor",
            "belowEditor" => "below-editor",
            "sidebar-left" => "sidebar-left",
            "sidebar-right" => "sidebar-right",
            _ => placement ?? "above-editor"
        };

    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
        => !((await bridge.HandleAsync(new ExtensionUiIntent(Guid.NewGuid().ToString("N"), "confirm", "Confirm", message, null, null), cancellationToken)).Cancelled);

    public async Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
        => (await bridge.HandleAsync(new ExtensionUiIntent(Guid.NewGuid().ToString("N"), "input", prompt, initialValue, null, null), cancellationToken)).Value?.ToString();

    public async Task<string?> OpenEditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default)
        => (await bridge.HandleAsync(new ExtensionUiIntent(Guid.NewGuid().ToString("N"), "editor", title, prefill, null, null), cancellationToken)).Value?.ToString();

    public async Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => _selectAsync is not null
            ? await _selectAsync(prompt, options, cancellationToken)
            : (await bridge.HandleAsync(new ExtensionUiIntent(Guid.NewGuid().ToString("N"), "select", prompt, null, options, null), cancellationToken)).Value?.ToString();

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
        => bridge.SetStatusAsync(extensionId, status, cancellationToken);

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
        => bridge.SetWidgetAsync(extensionId, widget, cancellationToken);

    public Task SetFooterAsync(string extensionId, ExtensionWidgetState? footer, CancellationToken cancellationToken = default)
        => bridge.SetFooterAsync(extensionId, footer, cancellationToken);

    public Task SetHeaderAsync(string extensionId, ExtensionWidgetState? header, CancellationToken cancellationToken = default)
        => bridge.SetHeaderAsync(extensionId, header, cancellationToken);

    public Task RegisterMenuItemAsync(string extensionId, ExtensionMenuItem item, CancellationToken cancellationToken = default)
        => bridge.RegisterMenuItemAsync(extensionId, item, cancellationToken);

    public Task SetTitleAsync(string extensionId, string? title, CancellationToken cancellationToken = default)
        => bridge.SetTitleAsync(title, cancellationToken);

    public Task<string?> GetEditorTextAsync(string extensionId, CancellationToken cancellationToken = default)
        => bridge.GetEditorTextAsync(cancellationToken);

    public Task SetEditorTextAsync(string extensionId, string text, CancellationToken cancellationToken = default)
        => bridge.SetEditorTextAsync(text, cancellationToken);

    public Task SetWorkingMessageAsync(string? message, CancellationToken cancellationToken = default)
        => bridge.SetWorkingMessageAsync(message, cancellationToken);

    public Task SetWorkingVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => bridge.SetWorkingVisibleAsync(visible, cancellationToken);

    public Task SetWorkingIndicatorAsync(ExtensionWorkingIndicator? indicator, CancellationToken cancellationToken = default)
        => bridge.SetWorkingIndicatorAsync(indicator, cancellationToken);
}
