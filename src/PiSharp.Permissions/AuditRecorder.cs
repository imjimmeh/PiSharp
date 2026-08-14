using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// <summary>
/// Optional audit for permission decisions: appends a <c>permission</c> session entry (P29 §8
/// <c>audit</c>) and emits the streaming <c>permission_blocked</c>/<c>permission_approved</c>
/// client events (P29 §4.4). Event emission is a no-op on hosts that do not wire the custom
/// event lane; entry append is best-effort on hosts without a session-entry surface.
/// </summary>
public sealed class AuditRecorder
{
    public const string EventBlocked = "permission_blocked";
    public const string EventApproved = "permission_approved";

    private readonly IExtensionApi _api;

    public AuditRecorder(IExtensionApi api)
    {
        _api = api;
    }

    public Task RecordBlockedAsync(
        string tool,
        string reason,
        string sessionKey,
        CancellationToken cancellationToken = default)
        => RecordAsync(new { tool, reason, sessionKey }, EventBlocked, cancellationToken);

    public Task RecordApprovedAsync(
        string tool,
        string sessionKey,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default)
        => RecordAsync(new { tool, sessionKey, durationSeconds }, EventApproved, cancellationToken);

    private async Task RecordAsync(object payload, string eventName, CancellationToken cancellationToken)
    {
        try
        {
            await _api.Session.AppendEntryAsync("permission", payload, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // Host without a session-entry append surface: audit is best-effort.
        }

        try
        {
            await _api.EmitClientEventAsync(eventName, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // Host without the custom event lane: event emission is optional.
        }
    }
}
