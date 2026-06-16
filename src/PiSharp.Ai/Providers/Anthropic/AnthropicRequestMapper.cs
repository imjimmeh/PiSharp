using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Anthropic;

internal static class AnthropicRequestMapper
{
    public static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
    {
        var messages = MessageTransformer.ToProviderMessages(context);
        var tools = ToolTransformer.ToProviderTools(context.Tools);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.Id);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tokens", options.MaxTokens ?? model.MaxTokens);
            if (options.Temperature is not null) writer.WriteNumber("temperature", options.Temperature.Value);
            var budget = ThinkingBudget(model, options);
            if (budget is not null)
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "enabled");
                writer.WriteNumber("budget_tokens", budget.Value);
                writer.WriteEndObject();
            }
            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, messages, ProviderJson.Options);
            ProviderToolSerializer.WriteAnthropicTools(writer, tools);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static int? ThinkingBudget(ModelDescriptor model, AgentStreamOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Reasoning) || model.ThinkingLevelMap is null) return null;
        return model.ThinkingLevelMap.TryGetValue(options.Reasoning, out var budget) ? budget : null;
    }
}
