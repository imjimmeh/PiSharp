using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Serialization;

namespace PiSharp.Agent.Messages;

public static class CustomMessageContent
{
    public static CustomMessage ToCustomMessage(string customType, object content, bool display, object? details = null)
    {
        var normalized = Normalize(content);
        return normalized.Blocks is { Count: > 0 }
            ? HarnessMessages.Custom(customType, normalized.Blocks, display, details)
            : HarnessMessages.Custom(customType, normalized.Text ?? string.Empty, display, details);
    }

    public static string ToDisplayText(object? content)
    {
        var normalized = Normalize(content);
        if (normalized.Text is { Length: > 0 })
        {
            return normalized.Text;
        }

        if (normalized.Blocks is null)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, normalized.Blocks.OfType<TextContent>().Select(b => b.Text));
    }

    public static NormalizedCustomContent Normalize(object? content)
    {
        if (content is null)
        {
            return new NormalizedCustomContent(string.Empty, null);
        }

        if (content is string text)
        {
            return new NormalizedCustomContent(text, null);
        }

        if (content is IReadOnlyList<MessageContent> blocks)
        {
            return new NormalizedCustomContent(null, blocks);
        }

        if (content is JsonElement element)
        {
            return NormalizeJsonElement(element);
        }

        return new NormalizedCustomContent(content.ToString() ?? string.Empty, null);
    }

    private static NormalizedCustomContent NormalizeJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return new NormalizedCustomContent(element.GetString() ?? string.Empty, null);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var blocks = JsonSerializer.Deserialize<IReadOnlyList<MessageContent>>(element.GetRawText(), AgentJsonSerializer.Options);
            return new NormalizedCustomContent(null, blocks ?? Array.Empty<MessageContent>());
        }

        return new NormalizedCustomContent(element.GetRawText(), null);
    }

    public sealed record NormalizedCustomContent(string? Text, IReadOnlyList<MessageContent>? Blocks);
}
