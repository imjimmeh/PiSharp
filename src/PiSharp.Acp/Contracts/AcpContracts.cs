using System.Text.Json;

namespace PiSharp.Acp;

// JSON-RPC 2.0 envelope records (plan §4.3). Envelopes are parsed from the wire into
// these shapes by <see cref="AcpServer"/>; responses are written by <see cref="AcpMessageWriter"/>.
public sealed record AcpRequest(string JsonRpc, object? Id, string Method, JsonElement? Params);

public sealed record AcpNotification(string JsonRpc, string Method, JsonElement? Params);

public sealed record AcpSuccessResponse(string JsonRpc, object? Id, object? Result);

public sealed record AcpErrorResponse(string JsonRpc, object? Id, AcpError Error);

public sealed record AcpError(int Code, string Message, object? Data = null);

/// <summary>ACP v1 method-name constants (plan §4.3).</summary>
public static class AcpMethods
{
    // client → agent
    public const string Initialize = "initialize";
    public const string SessionNew = "session/new";
    public const string SessionLoad = "session/load";
    public const string SessionResume = "session/resume";
    public const string SessionClose = "session/close";
    public const string SessionPrompt = "session/prompt";
    public const string SessionCancel = "session/cancel";          // notification
    // agent → client (requests the agent sends)
    public const string SessionRequestPermission = "session/request_permission";
    // notifications agent → client
    public const string SessionUpdate = "session/update";
}

/// <summary>Standard JSON-RPC error codes (plan §3.7).</summary>
public static class AcpErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int ServerError = -32000;
}

// Result records (plan §4.3; fields per the ACP v1 schema).
public sealed record AcpInitializeResult(
    int ProtocolVersion,
    AcpAgentCapabilities AgentCapabilities,
    AcpAgentInfo AgentInfo,
    IReadOnlyList<object> AuthMethods);

public sealed record AcpAgentCapabilities(
    bool LoadSession,
    AcpPromptCapabilities? PromptCapabilities,
    AcpSessionCapabilities? SessionCapabilities);

public sealed record AcpPromptCapabilities(
    bool Image = false,
    bool Audio = false,
    bool EmbeddedContext = false);

public sealed record AcpSessionCapabilities(object? Resume = null, object? Close = null);

public sealed record AcpAgentInfo(string Name, string Title, string Version);

public sealed record AcpSessionNewResult(string SessionId);

public sealed record AcpPromptResult(string StopReason);

public sealed record AcpPermissionRequestParams(
    string SessionId,
    AcpToolCallUpdate ToolCall,
    IReadOnlyList<AcpPermissionOption> Options);

public sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

public sealed record AcpPermissionResult(AcpPermissionOutcome Outcome);

public sealed record AcpPermissionOutcome(string Outcome, string? OptionId = null);

/// <summary>A tool-call update shared by <c>tool_call</c> and <c>tool_call_update</c> session updates.</summary>
public sealed record AcpToolCallUpdate(
    string ToolCallId,
    string Title,
    string Kind,
    string Status,
    object? RawInput = null,
    IReadOnlyList<object>? Content = null);
