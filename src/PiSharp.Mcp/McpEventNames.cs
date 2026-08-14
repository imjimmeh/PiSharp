namespace PiSharp.Mcp;

/// <summary>Plugin-defined event names emitted through <c>IExtensionApi.Events</c>.</summary>
public static class McpEventNames
{
    /// <summary>
    /// Fired whenever a server transitions state; payload is the serialized <see cref="McpServerStatus"/>.
    /// </summary>
    public const string McpServerStatus = "mcp_server_status";
}
