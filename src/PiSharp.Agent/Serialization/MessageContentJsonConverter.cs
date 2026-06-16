using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Serialization;

public sealed class MessageContentJsonConverter : JsonConverter<MessageContent>
{
    public override MessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return root.GetProperty("type").GetString() switch
        {
            "text" => new TextContent(
                root.GetProperty("text").GetString() ?? string.Empty,
                root.TryGetProperty("textSignature", out var ts) ? ts.GetString() : null),
            "image" => new ImageContent(
                root.GetProperty("mediaType").GetString() ?? string.Empty,
                root.GetProperty("data").GetString() ?? string.Empty),
            "thinking" => new ThinkingContent(
                root.GetProperty("thinking").GetString() ?? string.Empty,
                root.TryGetProperty("thinkingSignature", out var ths) ? ths.GetString() : null,
                root.TryGetProperty("redacted", out var redacted) && redacted.GetBoolean()),
            "toolCall" => new ToolCallContent(
                root.GetProperty("id").GetString() ?? string.Empty,
                root.GetProperty("name").GetString() ?? string.Empty,
                root.GetProperty("arguments").Clone(),
                root.TryGetProperty("thoughtSignature", out var thoughtSig) ? thoughtSig.GetString() : null),
            var type => throw new JsonException($"Unknown message content type '{type}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextContent text:
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                writer.WriteString("textSignature", text.TextSignature);
                break;
            case ImageContent image:
                writer.WriteString("type", "image");
                writer.WriteString("mediaType", image.MediaType);
                writer.WriteString("data", image.Data);
                break;
            case ThinkingContent thinking:
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", thinking.Thinking);
                writer.WriteString("thinkingSignature", thinking.ThinkingSignature);
                writer.WriteBoolean("redacted", thinking.Redacted);
                break;
            case ToolCallContent toolCall:
                writer.WriteString("type", "toolCall");
                writer.WriteString("id", toolCall.Id);
                writer.WriteString("name", toolCall.Name);
                writer.WritePropertyName("arguments");
                toolCall.Arguments.WriteTo(writer);
                writer.WriteString("thoughtSignature", toolCall.ThoughtSignature);
                break;
        }
        writer.WriteEndObject();
    }
}
