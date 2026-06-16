using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Testing;

public static class MiddlewareContextBuilder
{
    public static ExtensionMiddlewareContext Before(string toolName, JsonElement args)
    {
        var toolCall = new ToolCallContent("call-id", toolName, args);
        var assistantMessage = new AssistantMessage([toolCall]);
        var agentContext = new AgentContext("test system prompt", [assistantMessage]);
        var beforeToolCall = new BeforeToolCallContext(assistantMessage, toolCall, args, agentContext);

        return new ExtensionMiddlewareContext(
            new ExtensionEvent(ExtensionEventNames.ToolCall, new AgentHarnessEvent.Own(
                new AgentHarnessOwnEvent.ToolCall("call-id", toolName, new Dictionary<string, object?>()))),
            BeforeToolCall: beforeToolCall);
    }

    public static ExtensionMiddlewareContext After(
        string toolName,
        JsonElement args,
        bool isError = false,
        string? result = null)
    {
        var toolCall = new ToolCallContent("call-id", toolName, args);
        var assistantMessage = new AssistantMessage([toolCall]);
        var agentContext = new AgentContext("test system prompt", [assistantMessage]);
        var afterToolCall = new AfterToolCallContext(
            assistantMessage, toolCall, args,
            new AgentToolResult<object?>(Array.Empty<MessageContent>(), null, false),
            isError,
            agentContext);

        return new ExtensionMiddlewareContext(
            new ExtensionEvent(ExtensionEventNames.ToolCall, new AgentHarnessEvent.Own(
                new AgentHarnessOwnEvent.ToolCall("call-id", toolName, new Dictionary<string, object?>()))),
            AfterToolCall: afterToolCall);
    }
}
