using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;

namespace PiSharp.Cli.Modes;

public sealed record RpcResponse(string? Id, string Type, string Command, bool Success, object? Data = null, string? Error = null)
{
    public static RpcResponse Ok(string? id, string command, object? data = null) => new(id, "response", command, true, data);
    public static RpcResponse Fail(string? id, string command, string error) => new(id, "response", command, false, null, error);
}

public sealed record RpcSessionState(
    ModelDescriptor Model,
    ThinkingLevel ThinkingLevel,
    bool IsStreaming,
    bool IsCompacting,
    string SteeringMode,
    string FollowUpMode,
    string? SessionFile,
    string SessionId,
    string? SessionName,
    bool AutoCompactionEnabled,
    int MessageCount,
    int PendingMessageCount);

public sealed record RpcPromptCommand(string Type, string? Id, string Message, IReadOnlyList<ImageContent>? Images = null, string? StreamingBehavior = null);
public sealed record RpcExtensionUiRequest(string Id, string Kind, string Prompt, IReadOnlyList<string>? Options = null);
public sealed record RpcExtensionUiResponseCommand(string Type, string? Id, string RequestId, object? Value = null, bool Confirmed = false, bool Cancelled = false);
public sealed record RpcSetModelCommand(string Type, string? Id, string Provider, string ModelId);
public sealed record RpcSetThinkingLevelCommand(string Type, string? Id, string Level);
public sealed record RpcSessionNameCommand(string Type, string? Id, string Name);
public sealed record RpcEntryCommand(string Type, string? Id, string EntryId);
public sealed record RpcNewSessionResult(bool Cancelled);
public sealed record RpcAvailableModelsResult(IReadOnlyList<ModelDescriptor> Models);
public sealed record RpcMessagesResult(IReadOnlyList<AgentMessage> Messages);
public sealed record RpcLastAssistantTextResult([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text);
