using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Agent.Core.Loops;

public sealed record AgentLoopConfig(
    ModelDescriptor Model,
    AgentStreamAsync StreamAsync,
    Func<IReadOnlyList<AgentMessage>, CancellationToken, Task<IReadOnlyList<AgentMessage>>>? TransformContext = null,
    Func<IReadOnlyList<AgentMessage>, CancellationToken, Task<IReadOnlyList<AgentMessage>>>? ConvertToLlm = null,
    Func<string, CancellationToken, Task<string?>?>? GetApiKey = null,
    Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall = null,
    Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall = null,
    Func<PrepareNextTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn = null,
    Func<ShouldStopAfterTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurn = null,
    Func<CancellationToken, Task<IReadOnlyList<AgentMessage>>>? GetSteeringMessages = null,
    Func<CancellationToken, Task<IReadOnlyList<AgentMessage>>>? GetFollowUpMessages = null,
    ToolExecutionMode ToolExecution = ToolExecutionMode.Parallel,
    AgentStreamOptions? StreamOptions = null,
    ThinkingLevel ThinkingLevel = ThinkingLevel.Off,
    Func<StreamDeltaContext, CancellationToken, Task<StreamDeltaDecision?>>? OnStreamDelta = null,
    Func<IReadOnlyList<AgentMessage>, AgentContext, CancellationToken, Task<IReadOnlyList<AgentMessage>>>? PrepareStreamMessages = null,
    int MaxStreamRetries = 3);

public sealed record AgentLoopTurnUpdate(
    AgentContext? Context = null,
    ModelDescriptor? Model = null,
    ThinkingLevel? ThinkingLevel = null);

public sealed record ShouldStopAfterTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<AgentMessage> NewMessages);

public sealed record PrepareNextTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<AgentMessage> NewMessages);

public sealed record BeforeToolCallContext(
    AssistantMessage AssistantMessage,
    ToolCallContent ToolCall,
    JsonElement Args,
    AgentContext Context);

public sealed record AfterToolCallContext(
    AssistantMessage AssistantMessage,
    ToolCallContent ToolCall,
    JsonElement Args,
    AgentToolResult<object?> Result,
    bool IsError,
    AgentContext Context);
