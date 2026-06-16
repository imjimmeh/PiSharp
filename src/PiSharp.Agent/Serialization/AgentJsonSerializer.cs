using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Agent.Serialization;

public static class AgentJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value)
    {
        if (typeof(T) != typeof(object) || value is null) return JsonSerializer.Serialize(value, Options);
        return value switch
        {
            Core.Events.AgentHarnessEvent harnessEvent => JsonSerializer.Serialize(harnessEvent, Options),
            Core.Events.AgentSessionEvent sessionEvent => JsonSerializer.Serialize(sessionEvent, Options),
            Core.Events.AgentEvent agentEvent => JsonSerializer.Serialize(agentEvent, Options),
            Core.Streaming.AssistantMessageEvent assistantEvent => JsonSerializer.Serialize(assistantEvent, Options),
            Abstractions.Messages.AgentMessage message => JsonSerializer.Serialize(message, Options),
            _ => JsonSerializer.Serialize(value, value.GetType(), Options)
        };
    }

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        options.Converters.Add(new UsageInfoJsonConverter());
        options.Converters.Add(new AgentMessageJsonConverter());
        options.Converters.Add(new MessageContentJsonConverter());
        options.Converters.Add(new AssistantMessageEventJsonConverter());
        options.Converters.Add(new AgentEventJsonConverter());
        options.Converters.Add(new AgentSessionEventJsonConverter());
        options.Converters.Add(new AgentHarnessEventJsonConverter());
        options.Converters.Add(new SessionTreeEntryJsonConverter());
        return options;
    }

    public static string ToJsonLine(SessionTreeEntry entry)
        => Serialize(entry) + "\n";

    public static SessionTreeEntry ReadSessionEntry(string line)
        => Deserialize<SessionTreeEntry>(line) ?? throw new JsonException("Session entry deserialized to null.");
}
