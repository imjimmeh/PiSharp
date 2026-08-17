using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Ai.Providers.Shared;

public sealed record ProviderToolDefinition(string Name, string Description, JsonElement ParametersSchema);

public static class ToolTransformer
{
    private const int MaxToolCallIdLength = 64;

    public static IReadOnlyList<ProviderToolDefinition> ToProviderTools(IEnumerable<IAgentTool>? tools)
        => tools?.Select(tool => new ProviderToolDefinition(tool.Name, tool.Description, SanitizeSchema(tool.ParametersSchema))).ToArray()
           ?? [];

    public static JsonElement SanitizeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
            return doc.RootElement.Clone();
        }

        var node = JsonNode.Parse(schema.GetRawText());
        if (node is not JsonObject obj)
        {
            using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
            return doc.RootElement.Clone();
        }

        SanitizeNode(obj);
        if (!obj.ContainsKey("type"))
        {
            obj["type"] = "object";
        }

        using var sanitizedDoc = JsonDocument.Parse(obj.ToJsonString());
        return sanitizedDoc.RootElement.Clone();
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("enum") && !obj.ContainsKey("type"))
            {
                obj["type"] = "string";
            }
            else if (!obj.ContainsKey("type") && !obj.ContainsKey("$ref") && !obj.ContainsKey("anyOf") && !obj.ContainsKey("oneOf") && !obj.ContainsKey("allOf") && !obj.ContainsKey("properties") && !obj.ContainsKey("items"))
            {
                obj["type"] = "object";
            }

            var keys = obj.Select(kvp => kvp.Key).ToList();
            foreach (var key in keys)
            {
                var child = obj[key];
                if (key == "properties" && child is JsonObject propertiesObj)
                {
                    var propNames = propertiesObj.Select(p => p.Key).ToList();
                    foreach (var propName in propNames)
                    {
                        var propVal = propertiesObj[propName];
                        if (propVal is JsonValue val && val.TryGetValue<bool>(out var b) && b)
                        {
                            propertiesObj[propName] = new JsonObject { ["type"] = "object" };
                        }
                        else
                        {
                            SanitizeNode(propVal);
                        }
                    }
                }
                else if (key == "items")
                {
                    if (child is JsonValue val && val.TryGetValue<bool>(out var b) && b)
                    {
                        obj[key] = new JsonObject { ["type"] = "object" };
                    }
                    else
                    {
                        SanitizeNode(child);
                    }
                }
                else
                {
                    SanitizeNode(child);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (item is JsonValue val && val.TryGetValue<bool>(out var b) && b)
                {
                    arr[i] = new JsonObject { ["type"] = "object" };
                }
                else
                {
                    SanitizeNode(item);
                }
            }
        }
    }

    public static string NormalizeToolCallId(string? id)
    {
        if (IsValidToolCallId(id)) return id!;

        var seed = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant()[..24];
        return $"tc_{hash}";
    }

    public static bool IsValidToolCallId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length > MaxToolCallIdLength) return false;
        return id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    }

    public static JsonElement ParseArgumentsOrEmpty(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return EmptyObject();
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    public static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
