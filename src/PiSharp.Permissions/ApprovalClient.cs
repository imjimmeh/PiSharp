using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// <summary>
/// Issues typed <c>permission_request</c> approvals over the standard <see cref="IExtensionUi"/>
/// surface (P29 §4.3). The daemon's P01 <c>ui_request</c> lane backs this when a client is
/// attached; with no UI (headless daemon, print/rpc modes) the request resolves to Deny so
/// turns never hang and destructive calls are never silently run.
/// </summary>
public sealed class ApprovalClient
{
    public const string PermissionRequestKind = "permission_request";

    private readonly IExtensionApi _api;

    public ApprovalClient(IExtensionApi api)
    {
        _api = api;
    }

    /// <summary>
    /// Requests a verdict for a tool call. Returns <see cref="ApprovalVerdict.Deny"/> for
    /// headless hosts, failed UI requests (unrecognized kind), and transport errors — always
    /// the safe default.
    /// </summary>
    public async Task<ApprovalVerdict> RequestApprovalAsync(
        string tool,
        JsonElement arguments,
        string reason,
        string sessionKey,
        CancellationToken cancellationToken = default)
    {
        if (!_api.HasUi) return ApprovalVerdict.Deny;

        object payload;
        try
        {
            payload = JsonSerializer.SerializeToElement(new
            {
                tool,
                arguments,
                reason,
                defaultAnswer = "deny",
                sessionKey
            });
        }
        catch (JsonException)
        {
            payload = JsonSerializer.SerializeToElement(new
            {
                tool,
                reason,
                defaultAnswer = "deny",
                sessionKey
            });
        }

        try
        {
            var result = await _api.Ui.RequestAsync(
                new ExtensionUiRequest(_api.Descriptor.Id, PermissionRequestKind, (JsonElement)payload),
                cancellationToken).ConfigureAwait(false);
            if (!result.Ok) return ApprovalVerdict.Deny;
            return result.Value?.ToString() switch
            {
                "allow" => ApprovalVerdict.Allow,
                "allow_session" => ApprovalVerdict.AllowSession,
                _ => ApprovalVerdict.Deny
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // No UI backing (NoExtensionUi throws), transport failure, or client gone:
            // deny by default rather than risk a destructive call or a hung turn.
            return ApprovalVerdict.Deny;
        }
    }
}
