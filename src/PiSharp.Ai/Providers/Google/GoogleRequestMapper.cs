using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Google;

internal static class GoogleRequestMapper
{
    public static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
    {
        var tools = ToolTransformer.ToProviderTools(context.Tools);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("contents");
            JsonSerializer.Serialize(writer, MessageTransformer.ToProviderMessages(context, model.Input?.Contains("image") != false, flattenThinking: true), ProviderJson.Options);
            if (options.MaxTokens is not null || options.Temperature is not null)
            {
                writer.WritePropertyName("generationConfig");
                writer.WriteStartObject();
                if (options.MaxTokens is not null) writer.WriteNumber("maxOutputTokens", options.MaxTokens.Value);
                if (options.Temperature is not null) writer.WriteNumber("temperature", options.Temperature.Value);
                writer.WriteEndObject();
            }
            ProviderToolSerializer.WriteGoogleTools(writer, tools);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
