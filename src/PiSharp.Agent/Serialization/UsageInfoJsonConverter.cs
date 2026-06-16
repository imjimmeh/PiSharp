using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Serialization;

public sealed class UsageInfoJsonConverter : JsonConverter<UsageInfo>
{
    private const string InputPropertyName = "input";
    private const string OutputPropertyName = "output";
    private const string CacheReadPropertyName = "cacheRead";
    private const string CacheWritePropertyName = "cacheWrite";
    private const string TotalTokensPropertyName = "totalTokens";
    private const string CostPropertyName = "cost";
    private const string TotalPropertyName = "total";

    private static readonly UsageCost ZeroCost = new();

    public override UsageInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new UsageInfo(
            ReadInt(root, InputPropertyName),
            ReadInt(root, OutputPropertyName),
            ReadInt(root, CacheReadPropertyName),
            ReadInt(root, CacheWritePropertyName),
            ReadInt(root, TotalTokensPropertyName),
            ReadCostOrDefault(root));
    }

    public override void Write(Utf8JsonWriter writer, UsageInfo value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteTokenCounts(writer, value);
        WriteCost(writer, value.Cost ?? ZeroCost);
        writer.WriteEndObject();
    }

    private static UsageCost ReadCostOrDefault(JsonElement root)
        => root.TryGetProperty(CostPropertyName, out var cost) && cost.ValueKind == JsonValueKind.Object
            ? new UsageCost(
                ReadDecimal(cost, InputPropertyName),
                ReadDecimal(cost, OutputPropertyName),
                ReadDecimal(cost, CacheReadPropertyName),
                ReadDecimal(cost, CacheWritePropertyName),
                ReadDecimal(cost, TotalPropertyName))
            : ZeroCost;

    private static void WriteTokenCounts(Utf8JsonWriter writer, UsageInfo usage)
    {
        writer.WriteNumber(InputPropertyName, usage.Input);
        writer.WriteNumber(OutputPropertyName, usage.Output);
        writer.WriteNumber(CacheReadPropertyName, usage.CacheRead);
        writer.WriteNumber(CacheWritePropertyName, usage.CacheWrite);
        writer.WriteNumber(TotalTokensPropertyName, usage.TotalTokens);
    }

    private static void WriteCost(Utf8JsonWriter writer, UsageCost cost)
    {
        writer.WritePropertyName(CostPropertyName);
        writer.WriteStartObject();
        writer.WriteNumber(InputPropertyName, cost.Input);
        writer.WriteNumber(OutputPropertyName, cost.Output);
        writer.WriteNumber(CacheReadPropertyName, cost.CacheRead);
        writer.WriteNumber(CacheWritePropertyName, cost.CacheWrite);
        writer.WriteNumber(TotalPropertyName, cost.Total);
        writer.WriteEndObject();
    }

    private static int ReadInt(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static decimal ReadDecimal(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : 0m;
}
