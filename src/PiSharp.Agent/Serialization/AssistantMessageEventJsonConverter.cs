using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Agent.Serialization;

public sealed class AssistantMessageEventJsonConverter : JsonConverter<AssistantMessageEvent>
{
    public override AssistantMessageEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("AssistantMessageEvent JSON is emitted by providers and modes; RPC commands do not deserialize it.");

    public override void Write(Utf8JsonWriter writer, AssistantMessageEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case AssistantMessageEvent.Start start:
                writer.WriteString("type", "start");
                WritePartial(writer, start.Partial, options);
                break;
            case AssistantMessageEvent.TextStart start:
                writer.WriteString("type", "text_start");
                WritePartial(writer, start.Partial, options);
                writer.WriteNumber("contentIndex", start.ContentIndex);
                break;
            case AssistantMessageEvent.TextDelta delta:
                writer.WriteString("type", "text_delta");
                WritePartial(writer, delta.Partial, options);
                writer.WriteNumber("contentIndex", delta.ContentIndex);
                writer.WriteString("delta", delta.Delta);
                break;
            case AssistantMessageEvent.TextEnd end:
                writer.WriteString("type", "text_end");
                WritePartial(writer, end.Partial, options);
                writer.WriteNumber("contentIndex", end.ContentIndex);
                break;
            case AssistantMessageEvent.ThinkingStart start:
                writer.WriteString("type", "thinking_start");
                WritePartial(writer, start.Partial, options);
                writer.WriteNumber("contentIndex", start.ContentIndex);
                break;
            case AssistantMessageEvent.ThinkingDelta delta:
                writer.WriteString("type", "thinking_delta");
                WritePartial(writer, delta.Partial, options);
                writer.WriteNumber("contentIndex", delta.ContentIndex);
                writer.WriteString("delta", delta.Delta);
                break;
            case AssistantMessageEvent.ThinkingEnd end:
                writer.WriteString("type", "thinking_end");
                WritePartial(writer, end.Partial, options);
                writer.WriteNumber("contentIndex", end.ContentIndex);
                break;
            case AssistantMessageEvent.ToolCallStart start:
                writer.WriteString("type", "tool_call_start");
                WritePartial(writer, start.Partial, options);
                writer.WriteNumber("contentIndex", start.ContentIndex);
                break;
            case AssistantMessageEvent.ToolCallDelta delta:
                writer.WriteString("type", "tool_call_delta");
                WritePartial(writer, delta.Partial, options);
                writer.WriteNumber("contentIndex", delta.ContentIndex);
                writer.WriteString("delta", delta.Delta);
                break;
            case AssistantMessageEvent.ToolCallEnd end:
                writer.WriteString("type", "tool_call_end");
                WritePartial(writer, end.Partial, options);
                writer.WriteNumber("contentIndex", end.ContentIndex);
                writer.WritePropertyName("toolCall");
                JsonSerializer.Serialize(writer, end.ToolCall, options);
                break;
            case AssistantMessageEvent.Done done:
                writer.WriteString("type", "done");
                writer.WritePropertyName("message");
                JsonSerializer.Serialize(writer, done.Message, options);
                writer.WriteString("reason", done.Reason);
                break;
            case AssistantMessageEvent.Error error:
                writer.WriteString("type", "error");
                writer.WritePropertyName("errorMessage");
                JsonSerializer.Serialize(writer, error.ErrorMessage, options);
                writer.WriteString("reason", error.Reason);
                break;
            default:
                throw new JsonException($"Unsupported AssistantMessageEvent type {value.GetType().Name}.");
        }
        writer.WriteEndObject();
    }

    private static void WritePartial(Utf8JsonWriter writer, object partial, JsonSerializerOptions options)
    {
        writer.WritePropertyName("partial");
        JsonSerializer.Serialize(writer, partial, options);
    }
}
