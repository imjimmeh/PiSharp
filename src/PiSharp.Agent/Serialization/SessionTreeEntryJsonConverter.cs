using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Agent.Serialization;

public sealed class SessionTreeEntryJsonConverter : JsonConverter<SessionTreeEntry>
{
    public override SessionTreeEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var common = ReadCommon(root);
        var type = root.GetProperty("type").GetString();
        return type switch
        {
            MessageEntry.TypeName => new MessageEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, Message = root.GetProperty("message").Deserialize<AgentMessage>(options)! },
            ThinkingLevelChangeEntry.TypeName => new ThinkingLevelChangeEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, ThinkingLevel = root.GetProperty("thinkingLevel").GetString() ?? "off" },
            ModelChangeEntry.TypeName => new ModelChangeEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, Provider = root.GetProperty("provider").GetString() ?? string.Empty, ModelId = root.GetProperty("modelId").GetString() ?? string.Empty },
            CompactionEntry.TypeName => new CompactionEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, Summary = root.GetProperty("summary").GetString() ?? string.Empty, FirstKeptEntryId = root.GetProperty("firstKeptEntryId").GetString() ?? string.Empty, TokensBefore = root.GetProperty("tokensBefore").GetInt32(), Details = ReadOptional(root, "details"), FromHook = ReadOptionalBool(root, "fromHook") },
            BranchSummaryEntry.TypeName => new BranchSummaryEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, FromId = root.GetProperty("fromId").GetString() ?? string.Empty, Summary = root.GetProperty("summary").GetString() ?? string.Empty, Details = ReadOptional(root, "details"), FromHook = ReadOptionalBool(root, "fromHook") },
            CustomEntry.TypeName => new CustomEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, CustomType = root.GetProperty("customType").GetString() ?? string.Empty, Data = ReadOptional(root, "data") },
            CustomMessageEntry.TypeName => new CustomMessageEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, CustomType = root.GetProperty("customType").GetString() ?? string.Empty, Content = ReadOptional(root, "content") ?? string.Empty, Details = ReadOptional(root, "details"), Display = root.TryGetProperty("display", out var display) && display.GetBoolean() },
            LabelEntry.TypeName => new LabelEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, TargetId = root.GetProperty("targetId").GetString() ?? string.Empty, Label = root.TryGetProperty("label", out var label) ? label.GetString() : null },
            SessionInfoEntry.TypeName => new SessionInfoEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, Name = root.TryGetProperty("name", out var name) ? name.GetString() : null },
            LeafEntry.TypeName => new LeafEntry { Id = common.Id, ParentId = common.ParentId, Timestamp = common.Timestamp, TargetId = root.TryGetProperty("targetId", out var target) ? target.GetString() : null },
            _ => throw new JsonException($"Unknown session entry type '{type}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionTreeEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteString("id", value.Id);
        writer.WriteString("parentId", value.ParentId);
        writer.WriteString("timestamp", value.Timestamp);
        switch (value)
        {
            case MessageEntry entry:
                writer.WritePropertyName("message");
                JsonSerializer.Serialize(writer, entry.Message, options);
                break;
            case ThinkingLevelChangeEntry entry:
                writer.WriteString("thinkingLevel", entry.ThinkingLevel);
                break;
            case ModelChangeEntry entry:
                writer.WriteString("provider", entry.Provider);
                writer.WriteString("modelId", entry.ModelId);
                break;
            case CompactionEntry entry:
                writer.WriteString("summary", entry.Summary);
                writer.WriteString("firstKeptEntryId", entry.FirstKeptEntryId);
                writer.WriteNumber("tokensBefore", entry.TokensBefore);
                WriteOptional(writer, "details", entry.Details, options);
                WriteOptional(writer, "fromHook", entry.FromHook, options);
                break;
            case BranchSummaryEntry entry:
                writer.WriteString("fromId", entry.FromId);
                writer.WriteString("summary", entry.Summary);
                WriteOptional(writer, "details", entry.Details, options);
                WriteOptional(writer, "fromHook", entry.FromHook, options);
                break;
            case CustomEntry entry:
                writer.WriteString("customType", entry.CustomType);
                WriteOptional(writer, "data", entry.Data, options);
                break;
            case CustomMessageEntry entry:
                writer.WriteString("customType", entry.CustomType);
                WriteOptional(writer, "content", entry.Content, options);
                WriteOptional(writer, "details", entry.Details, options);
                writer.WriteBoolean("display", entry.Display);
                break;
            case LabelEntry entry:
                writer.WriteString("targetId", entry.TargetId);
                writer.WriteString("label", entry.Label);
                break;
            case SessionInfoEntry entry:
                writer.WriteString("name", entry.Name);
                break;
            case LeafEntry entry:
                writer.WriteString("targetId", entry.TargetId);
                break;
        }
        writer.WriteEndObject();
    }

    private static (string Id, string? ParentId, DateTimeOffset Timestamp) ReadCommon(JsonElement root)
    {
        var id = TryGetPropertyString(root, "id") ?? TryGetPropertyString(root, "Id") ?? string.Empty;
        var parentId = TryGetPropertyString(root, "parentId") ?? TryGetPropertyString(root, "ParentId");
        var timestampStr = TryGetPropertyString(root, "timestamp") ?? TryGetPropertyString(root, "Timestamp");
        var timestamp = timestampStr is not null && DateTimeOffset.TryParse(timestampStr, out var parsedTs)
            ? parsedTs
            : DateTimeOffset.UtcNow;
        return (id, parentId, timestamp);
    }

    private static string? TryGetPropertyString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static object? ReadOptional(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;

    private static bool? ReadOptionalBool(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetBoolean() : null;

    private static void WriteOptional(Utf8JsonWriter writer, string property, object? value, JsonSerializerOptions options)
    {
        if (value is null) return;
        writer.WritePropertyName(property);
        JsonSerializer.Serialize(writer, value, options);
    }
}
