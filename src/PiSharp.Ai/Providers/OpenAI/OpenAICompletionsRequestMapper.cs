using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.OpenAI;

internal static class OpenAICompletionsRequestMapper
{
    public static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
    {
        var tools = ToolTransformer.ToProviderTools(context.Tools);
        var supportsImages = model.Input?.Contains("image") != false;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.Id);
            writer.WriteBoolean("stream", true);
            writer.WritePropertyName("messages");
            WriteOpenAIChatMessages(writer, context, supportsImages);
            writer.WriteNumber("max_tokens", options.MaxTokens ?? model.MaxTokens);
            if (options.Temperature is not null) writer.WriteNumber("temperature", options.Temperature.Value);
            ProviderToolSerializer.WriteOpenAIChatTools(writer, tools);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteOpenAIChatMessages(Utf8JsonWriter writer, AgentContext context, bool supportsImages)
    {
        var messages = MessageTransformer.ToProviderMessages(context, supportsImages, flattenThinking: true);
        writer.WriteStartArray();
        foreach (var message in messages)
        {
            if (message.Role == "assistant")
            {
                WriteAssistantMessage(writer, message.Content);
                continue;
            }

            if (message.Role == "tool")
            {
                WriteToolResultMessages(writer, message.Content);
                continue;
            }

            WriteStandardMessage(writer, message.Role, message.Content, supportsImages);
        }
        writer.WriteEndArray();
    }

    private static void WriteStandardMessage(Utf8JsonWriter writer, string role, IReadOnlyList<ProviderContent> content, bool supportsImages)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);

        if (role == "user")
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var item in content)
            {
                if (item.Type == "text" && !string.IsNullOrEmpty(item.Text))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", item.Text);
                    writer.WriteEndObject();
                    continue;
                }

                if (supportsImages && item.Type == "image" && !string.IsNullOrWhiteSpace(item.Data))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WritePropertyName("image_url");
                    writer.WriteStartObject();
                    writer.WriteString("url", $"data:{item.MediaType ?? "application/octet-stream"};base64,{item.Data}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("content", JoinText(content));
        }

        writer.WriteEndObject();
    }

    private static void WriteAssistantMessage(Utf8JsonWriter writer, IReadOnlyList<ProviderContent> content)
    {
        var toolCalls = content.Where(item => item.Type == "tool_call" && !string.IsNullOrWhiteSpace(item.Name)).ToArray();
        var text = JoinText(content);

        writer.WriteStartObject();
        writer.WriteString("role", "assistant");

        if (toolCalls.Length == 0)
        {
            writer.WriteString("content", text);
            writer.WriteEndObject();
            return;
        }

        if (text.Length == 0) writer.WriteNull("content");
        else writer.WriteString("content", text);

        writer.WritePropertyName("tool_calls");
        writer.WriteStartArray();
        foreach (var toolCall in toolCalls)
        {
            writer.WriteStartObject();
            writer.WriteString("id", string.IsNullOrWhiteSpace(toolCall.Id) ? Guid.NewGuid().ToString("N") : toolCall.Id);
            writer.WriteString("type", "function");
            writer.WritePropertyName("function");
            writer.WriteStartObject();
            writer.WriteString("name", toolCall.Name);
            writer.WriteString("arguments", SerializeArguments(toolCall.Arguments));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteToolResultMessages(Utf8JsonWriter writer, IReadOnlyList<ProviderContent> content)
    {
        var toolResults = content.Where(item => item.Type == "tool_result").ToArray();
        foreach (var toolResult in toolResults)
        {
            writer.WriteStartObject();
            writer.WriteString("role", "tool");
            if (!string.IsNullOrWhiteSpace(toolResult.ToolUseId)) writer.WriteString("tool_call_id", toolResult.ToolUseId);
            writer.WriteString("content", toolResult.Text ?? string.Empty);
            writer.WriteEndObject();
        }
    }

    private static string SerializeArguments(JsonElement? arguments)
    {
        if (arguments is null) return "{}";
        var value = arguments.Value;
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : value.GetRawText();
    }

    private static string JoinText(IEnumerable<ProviderContent> content)
        => string.Join("\n", content.Where(item => item.Type == "text" && !string.IsNullOrEmpty(item.Text)).Select(item => item.Text));
}
