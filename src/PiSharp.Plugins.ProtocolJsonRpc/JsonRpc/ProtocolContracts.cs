using System.Text.Json;

namespace PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

/// <summary>
/// Wire message shape. JSON-RPC (LSP) messages carry <c>id</c>/<c>method</c>/<c>result</c>;
/// DAP messages carry <c>seq</c>/<c>type</c>/<c>request_seq</c>/<c>command</c>/<c>body</c>
/// and correlate responses by <c>request_seq</c>. Both use the same Content-Length framing.
/// </summary>
public enum RpcFrameShape
{
    JsonRpc,
    Dap,
}

/// <summary>
/// A message read from the wire. For JSON-RPC: <see cref="Id"/> is null for notifications,
/// <see cref="Method"/> is null for responses, <see cref="Params"/> carries request/notification
/// parameters (responses are dispatched to the pending map and never surface here). For DAP:
/// responses never surface here; requests surface with <see cref="Id"/> = <c>seq</c> and
/// <see cref="Method"/> = <c>command</c>; events surface as notifications with
/// <see cref="Method"/> = <c>event:&lt;name&gt;</c> and <see cref="Params"/> = <c>body</c>.
/// </summary>
public sealed record InboundRpcMessage(
    string? Id,
    string? Method,
    JsonElement? Params,
    bool IsNotification);

/// <summary>
/// A protocol error object. Returned from an inbound handler to answer a request with an
/// error response (JSON-RPC <c>error</c> member, or DAP <c>success: false</c> response).
/// </summary>
public sealed record JsonRpcError(int Code, string Message, object? Data = null);

/// <summary>
/// Thrown when a remote response carries an error (JSON-RPC <c>error</c> member, or DAP
/// <c>success: false</c>), and when the peer faults a request (process exit, pump failure,
/// malformed frame).
/// </summary>
public sealed class JsonRpcRemoteException(int Code, string Message, JsonElement? Data = null) : Exception(Message)
{
    public int Code { get; } = Code;

    /// <summary>Optional error payload; shadows <see cref="Exception.Data"/> deliberately.</summary>
    public new JsonElement? Data { get; } = Data;
}
