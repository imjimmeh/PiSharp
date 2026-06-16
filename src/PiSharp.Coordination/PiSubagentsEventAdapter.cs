using System.Text.Json;

namespace PiSharp.Coordination;

public static class PiSubagentsEventAdapter
{
    public const int MaxTypeLength = 256;
    public const int MaxDescriptionLength = 1024;
    public const int MaxStatusLength = 64;

    private static readonly HashSet<string> KnownEventNames = new(StringComparer.Ordinal)
    {
        "subagents:created",
        "subagents:started",
        "subagents:completed",
        "subagents:failed",
        "subagents:steered",
        "subagents:compacted",
    };

    public static bool IsKnownEventName(string eventName) => KnownEventNames.Contains(eventName);

    public static SubagentObservedRecord? TryMap(string eventName, object? payload, string? parentSessionId, string cwd)
    {
        if (!KnownEventNames.Contains(eventName))
            return null;

        if (payload is not JsonElement element)
        {
            if (payload is JsonDocument doc)
                element = doc.RootElement;
            else
                return null;
        }

        return TryMapFromElement(eventName, element, parentSessionId, cwd);
    }

    public static SubagentObservedRecord? TryMap(string eventName, JsonElement element, string? parentSessionId, string cwd)
    {
        if (!KnownEventNames.Contains(eventName))
            return null;

        return TryMapFromElement(eventName, element, parentSessionId, cwd);
    }

    private static SubagentObservedRecord? TryMapFromElement(string eventName, JsonElement payload, string? parentSessionId, string cwd)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        if (!payload.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            return null;

        var subagentId = idElement.GetString();
        if (string.IsNullOrWhiteSpace(subagentId))
            return null;

        var subagentType = Truncate(GetOptionalString(payload, "type"), MaxTypeLength);
        var description = Truncate(GetOptionalString(payload, "description"), MaxDescriptionLength);
        var status = Truncate(GetOptionalString(payload, "status"), MaxStatusLength);
        var durationMs = GetOptionalDouble(payload, "durationMs");
        var toolUses = GetOptionalInt32(payload, "toolUses");
        var inputTokens = GetOptionalInt32(payload, "inputTokens");
        var outputTokens = GetOptionalInt32(payload, "outputTokens");
        var timestamp = DateTimeOffset.UtcNow;

        return new SubagentObservedRecord(
            subagentId,
            subagentType,
            description,
            status,
            eventName,
            durationMs,
            toolUses,
            inputTokens,
            outputTokens,
            parentSessionId,
            cwd,
            timestamp);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetDouble(out var value))
                return value;
        }
        return null;
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetInt32(out var value))
                return value;
        }
        return null;
    }
}
