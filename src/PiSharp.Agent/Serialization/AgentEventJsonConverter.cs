using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Agent.Core.Events;

namespace PiSharp.Agent.Serialization;

public sealed class AgentEventJsonConverter : JsonConverter<AgentEvent>
{
    public override AgentEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("AgentEvent JSON is emitted by PiSharp.Cli; command input does not deserialize AgentEvent payloads.");

    public override void Write(Utf8JsonWriter writer, AgentEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case AgentEvent.AgentStart:
                writer.WriteString("type", "agent_start");
                break;
            case AgentEvent.AgentEnd end:
                writer.WriteString("type", "agent_end");
                writer.WritePropertyName("messages");
                JsonSerializer.Serialize(writer, end.Messages, options);
                break;
            case AgentEvent.TurnStart:
                writer.WriteString("type", "turn_start");
                break;
            case AgentEvent.TurnEnd turn:
                writer.WriteString("type", "turn_end");
                writer.WritePropertyName("message");
                JsonSerializer.Serialize(writer, turn.Message, options);
                writer.WritePropertyName("toolResults");
                JsonSerializer.Serialize(writer, turn.ToolResults, options);
                break;
            case AgentEvent.MessageStart start:
                writer.WriteString("type", "message_start");
                writer.WritePropertyName("message");
                JsonSerializer.Serialize(writer, start.Message, options);
                break;
            case AgentEvent.MessageUpdate update:
                writer.WriteString("type", "message_update");
                writer.WritePropertyName("message");
                JsonSerializer.Serialize(writer, update.Message, options);
                writer.WritePropertyName("assistantMessageEvent");
                JsonSerializer.Serialize(writer, update.AssistantMessageEvent, options);
                break;
            case AgentEvent.MessageEnd end:
                writer.WriteString("type", "message_end");
                writer.WritePropertyName("message");
                JsonSerializer.Serialize(writer, end.Message, options);
                break;
            case AgentEvent.ToolExecutionStart tool:
                writer.WriteString("type", "tool_execution_start");
                writer.WriteString("toolCallId", tool.ToolCallId);
                writer.WriteString("toolName", tool.ToolName);
                writer.WritePropertyName("arguments");
                tool.Arguments.WriteTo(writer);
                break;
            case AgentEvent.ToolExecutionUpdate tool:
                writer.WriteString("type", "tool_execution_update");
                writer.WriteString("toolCallId", tool.ToolCallId);
                writer.WriteString("toolName", tool.ToolName);
                writer.WritePropertyName("arguments");
                tool.Arguments.WriteTo(writer);
                writer.WritePropertyName("partialResult");
                JsonSerializer.Serialize(writer, tool.PartialResult, options);
                break;
            case AgentEvent.ToolExecutionEnd tool:
                writer.WriteString("type", "tool_execution_end");
                writer.WriteString("toolCallId", tool.ToolCallId);
                writer.WriteString("toolName", tool.ToolName);
                writer.WritePropertyName("result");
                JsonSerializer.Serialize(writer, tool.Result, options);
                writer.WriteBoolean("isError", tool.IsError);
                break;
            default:
                throw new JsonException($"Unsupported AgentEvent type {value.GetType().Name}.");
        }
        writer.WriteEndObject();
    }
}
