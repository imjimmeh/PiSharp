using System.Text.Json;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;

namespace PiSharp.Server.UiBridge;

/// <summary>
/// Daemon-side <see cref="IExtensionUi"/>: forwards extension UI requests to the owning session's
/// <c>ui_request</c> lane via <see cref="IServerUiBridge"/>, so an attached interactive client can
/// answer permission approvals, confirmations, and custom UI. With no client attached it denies
/// immediately — no round-trip latency, no orphan <c>ui_request</c> events — keeping headless and
/// SDK-session behavior identical to the previous hard deny.
/// </summary>
public sealed class DaemonExtensionUi(LiveServerSession live, IServerUiBridge bridge) : IExtensionUi
{
    public async Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
    {
        if (live.AttachedClients == 0)
            return new ExtensionUiResult(false, Error: "No interactive client is attached to this daemon session.");

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

        // responseTimeout: null lets the bridge apply Task 9's per-kind policy (5 min interactive,
        // 5s otherwise) instead of a fixed window.
        var response = await bridge.RequestUiAsync(intent, live, responseTimeout: null, cancellationToken).ConfigureAwait(false);
        return new ExtensionUiResult(!response.Cancelled, response.Value?.ToString(), Error: response.Cancelled ? "UI request cancelled (no client answered)." : null);
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
