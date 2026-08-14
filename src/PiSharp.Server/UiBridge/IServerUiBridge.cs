using PiSharp.Server.Contracts;

namespace PiSharp.Server.UiBridge;

/// <summary>
/// Bidirectional bridge for extension UI requests between the daemon and attached clients.
/// Server-side callers register a pending request (keyed by <see cref="ServerUiIntent.RequestId"/>);
/// the request is pushed to the target session as a <c>ui_request</c> event, and a client's
/// <c>ui_response</c> command completes it via <see cref="ResolveUiAsync"/>. When no client answers
/// within the bridge's response timeout, the pending request resolves as cancelled so extension
/// turns do not hang.
/// </summary>
public interface IServerUiBridge
{
    Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, CancellationToken cancellationToken = default);
    void ResolveUiAsync(string requestId, string? value, bool cancelled);
}
