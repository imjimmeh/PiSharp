using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Agent.Core.Events;

namespace PiSharp.Agent.Serialization;

public sealed class AgentSessionEventJsonConverter : JsonConverter<AgentSessionEvent>
{
    public override AgentSessionEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("AgentSessionEvent is a write-only RPC protocol shape.");

    public override void Write(Utf8JsonWriter writer, AgentSessionEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);

        if (value.Data is not null)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value.Data, options));
            foreach (var property in doc.RootElement.EnumerateObject()) property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

public sealed class AgentHarnessEventJsonConverter : JsonConverter<AgentHarnessEvent>
{
    public override AgentHarnessEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("AgentHarnessEvent JSON is an output protocol shape.");

    public override void Write(Utf8JsonWriter writer, AgentHarnessEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case AgentHarnessEvent.Core core:
                writer.WriteString("type", "core");
                writer.WritePropertyName("event");
                JsonSerializer.Serialize(writer, core.Event, options);
                break;
            case AgentHarnessEvent.Own own:
                writer.WriteString("type", "own");
                writer.WritePropertyName("event");
                WriteOwnEvent(writer, own.Event, options);
                break;
            default:
                throw new JsonException($"Unsupported AgentHarnessEvent type {value.GetType().Name}.");
        }
        writer.WriteEndObject();
    }

    private static void WriteOwnEvent(Utf8JsonWriter writer, AgentHarnessOwnEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case AgentHarnessOwnEvent.ModelSelect model:
                writer.WriteString("type", "model_select");
                writer.WritePropertyName("model");
                JsonSerializer.Serialize(writer, model.Model, options);
                writer.WritePropertyName("previousModel");
                JsonSerializer.Serialize(writer, model.PreviousModel, options);
                writer.WriteString("source", model.Source);
                break;
            case AgentHarnessOwnEvent.ThinkingLevelSelect thinking:
                writer.WriteString("type", "thinking_level_select");
                writer.WriteString("level", thinking.Level.ToString().ToLowerInvariant());
                writer.WriteString("previousLevel", thinking.PreviousLevel.ToString().ToLowerInvariant());
                break;
            case AgentHarnessOwnEvent.QueueUpdate queue:
                writer.WriteString("type", "queue_update");
                writer.WritePropertyName("steer");
                JsonSerializer.Serialize(writer, queue.Steer, options);
                writer.WritePropertyName("followUp");
                JsonSerializer.Serialize(writer, queue.FollowUp, options);
                writer.WritePropertyName("nextTurn");
                JsonSerializer.Serialize(writer, queue.NextTurn, options);
                break;
            case AgentHarnessOwnEvent.CompactionStart compactionStart:
                writer.WriteString("type", "compaction_start");
                writer.WriteString("reason", compactionStart.Reason);
                break;
            case AgentHarnessOwnEvent.CompactionEnd compactionEnd:
                writer.WriteString("type", "compaction_end");
                writer.WriteString("reason", compactionEnd.Reason);
                writer.WritePropertyName("result");
                JsonSerializer.Serialize(writer, compactionEnd.Result, options);
                writer.WriteBoolean("aborted", compactionEnd.Aborted);
                writer.WriteBoolean("willRetry", compactionEnd.WillRetry);
                if (compactionEnd.ErrorMessage is not null) writer.WriteString("errorMessage", compactionEnd.ErrorMessage);
                break;
            case AgentHarnessOwnEvent.AutoRetryStart autoRetryStart:
                writer.WriteString("type", "auto_retry_start");
                writer.WriteNumber("attempt", autoRetryStart.Attempt);
                writer.WriteNumber("maxAttempts", autoRetryStart.MaxAttempts);
                writer.WriteNumber("delayMs", autoRetryStart.DelayMs);
                writer.WriteString("errorMessage", autoRetryStart.ErrorMessage);
                break;
            case AgentHarnessOwnEvent.AutoRetryEnd autoRetryEnd:
                writer.WriteString("type", "auto_retry_end");
                writer.WriteBoolean("success", autoRetryEnd.Success);
                writer.WriteNumber("attempt", autoRetryEnd.Attempt);
                if (autoRetryEnd.FinalError is not null) writer.WriteString("finalError", autoRetryEnd.FinalError);
                break;
            case AgentHarnessOwnEvent.SessionInfoChanged sessionInfo:
                writer.WriteString("type", "session_info_changed");
                writer.WriteString("name", sessionInfo.Name);
                break;
            case AgentHarnessOwnEvent.ThinkingLevelChanged thinkingLevel:
                writer.WriteString("type", "thinking_level_changed");
                writer.WriteString("level", thinkingLevel.Level.ToString().ToLowerInvariant());
                break;
            default:
                writer.WriteString("type", ToSnakeCase(value.GetType().Name));
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
        writer.WriteEndObject();
    }

    private static string ToSnakeCase(string name)
    {
        var chars = new List<char>();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) chars.Add('_');
            chars.Add(char.ToLowerInvariant(c));
        }
        return new string(chars.ToArray());
    }
}
