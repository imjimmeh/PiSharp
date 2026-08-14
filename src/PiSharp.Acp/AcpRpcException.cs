namespace PiSharp.Acp;

/// <summary>
/// A JSON-RPC application error thrown by the ACP engine. <see cref="AcpServer"/> catches it and
/// maps it to an <c>error</c> response with the associated JSON-RPC code (§3.7).
/// </summary>
public sealed class AcpRpcException(int code, string message, object? data = null) : Exception(message)
{
    public int Code { get; } = code;

    public object? Data { get; } = data;

    public static AcpRpcException InvalidParams(string message) => new(AcpErrorCodes.InvalidParams, message);

    public static AcpRpcException Server(string message, object? data = null) => new(AcpErrorCodes.ServerError, message, data);
}
