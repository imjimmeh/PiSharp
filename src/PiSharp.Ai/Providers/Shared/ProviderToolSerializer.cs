using System.Text.Json;

namespace PiSharp.Ai.Providers.Shared;

public static class ProviderToolSerializer
{
    public static void WriteAnthropicTools(Utf8JsonWriter writer, IReadOnlyList<ProviderToolDefinition> tools)
    {
        if (tools.Count == 0) return;
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("input_schema");
            tool.ParametersSchema.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static void WriteOpenAIResponsesTools(Utf8JsonWriter writer, IReadOnlyList<ProviderToolDefinition> tools)
    {
        if (tools.Count == 0) return;
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            tool.ParametersSchema.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static void WriteOpenAIChatTools(Utf8JsonWriter writer, IReadOnlyList<ProviderToolDefinition> tools)
    {
        if (tools.Count == 0) return;
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WritePropertyName("function");
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            tool.ParametersSchema.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static void WriteGoogleTools(Utf8JsonWriter writer, IReadOnlyList<ProviderToolDefinition> tools)
    {
        if (tools.Count == 0) return;
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WritePropertyName("functionDeclarations");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            tool.ParametersSchema.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    public static void WriteBedrockToolConfig(Utf8JsonWriter writer, IReadOnlyList<ProviderToolDefinition> tools)
    {
        if (tools.Count == 0) return;
        writer.WritePropertyName("toolConfig");
        writer.WriteStartObject();
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("toolSpec");
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            writer.WriteStartObject();
            writer.WritePropertyName("json");
            tool.ParametersSchema.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteMistralTools(Utf8JsonWriter writer, IReadOnlyList<ProviderToolDefinition> tools) => WriteOpenAIChatTools(writer, tools);
}
