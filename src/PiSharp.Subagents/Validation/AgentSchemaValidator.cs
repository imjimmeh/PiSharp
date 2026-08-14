using System.Text.Json;

namespace PiSharp.Subagents.Validation;

/// <summary>
/// Minimal JSON Schema validator covering the subset agent authors need: <c>type</c>,
/// <c>properties</c>, <c>required</c>, <c>items</c>, <c>enum</c>, <c>minItems</c>, and
/// <c>additionalProperties</c>. Deliberately small — the frontmatter <c>output</c> schema is
/// authored directly as a JSON Schema document (plan §3.5).
/// </summary>
public static class AgentSchemaValidator
{
    /// <summary>Validates <paramref name="instance"/> against <paramref name="schema"/>.
    /// A null/undefined schema accepts anything. Errors are human-readable paths like
    /// <c>findings[0].severity</c>.</summary>
    public static bool Validate(JsonElement? schema, JsonElement instance, out IReadOnlyList<string> errors)
    {
        if (schema is not { } document)
        {
            errors = [];
            return true;
        }

        var collected = new List<string>();
        ValidateNode(document, instance, "$", collected);
        errors = collected;
        return collected.Count == 0;
    }

    private static void ValidateNode(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String)
        {
            var expected = typeNode.GetString();
            if (expected is not null && !MatchesType(expected, instance))
            {
                errors.Add($"{path}: expected type '{expected}', got '{DescribeType(instance)}'");
                // Type mismatch short-circuits deeper structural checks.
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var enumNode) && enumNode.ValueKind == JsonValueKind.Array)
        {
            var matches = false;
            foreach (var candidate in enumNode.EnumerateArray())
            {
                if (JsonElement.DeepEquals(candidate, instance))
                {
                    matches = true;
                    break;
                }
            }
            if (!matches)
                errors.Add($"{path}: value is not one of the allowed enum values");
        }

        if (instance.ValueKind == JsonValueKind.Object)
            ValidateObject(schema, instance, path, errors);
        else if (instance.ValueKind == JsonValueKind.Array)
            ValidateArray(schema, instance, path, errors);
    }

    private static void ValidateObject(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var required in requiredNode.EnumerateArray())
            {
                if (required.ValueKind != JsonValueKind.String)
                    continue;
                var propertyName = required.GetString();
                if (propertyName is not null && !instance.TryGetProperty(propertyName, out _))
                    errors.Add($"{path}: missing required property '{propertyName}'");
            }
        }

        if (schema.TryGetProperty("properties", out var propertiesNode) && propertiesNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in propertiesNode.EnumerateObject())
            {
                if (instance.TryGetProperty(property.Name, out var propertyValue))
                    ValidateNode(property.Value, propertyValue, $"{path}.{property.Name}", errors);
            }
        }

        if (schema.TryGetProperty("additionalProperties", out var additionalNode)
            && additionalNode.ValueKind == JsonValueKind.False)
        {
            foreach (var property in instance.EnumerateObject())
            {
                var declared = schema.TryGetProperty("properties", out var declaredNode)
                    && declaredNode.ValueKind == JsonValueKind.Object
                    && declaredNode.TryGetProperty(property.Name, out _);
                if (!declared)
                    errors.Add($"{path}: additional property '{property.Name}' is not allowed");
            }
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minItems", out var minItemsNode) && minItemsNode.ValueKind == JsonValueKind.Number)
        {
            var minItems = minItemsNode.GetInt32();
            var count = 0;
            foreach (var _ in instance.EnumerateArray())
                count++;
            if (count < minItems)
                errors.Add($"{path}: array has {count} items, expected at least {minItems}");
        }

        if (schema.TryGetProperty("items", out var itemsNode) && itemsNode.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var element in instance.EnumerateArray())
            {
                ValidateNode(itemsNode, element, $"{path}[{index}]", errors);
                index++;
            }
        }
    }

    private static bool MatchesType(string expected, JsonElement instance)
    {
        return expected switch
        {
            "object" => instance.ValueKind == JsonValueKind.Object,
            "array" => instance.ValueKind == JsonValueKind.Array,
            "string" => instance.ValueKind == JsonValueKind.String,
            "number" => instance.ValueKind == JsonValueKind.Number,
            "integer" => instance.ValueKind == JsonValueKind.Number && IsInteger(instance),
            "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => instance.ValueKind == JsonValueKind.Null,
            _ => true,
        };
    }

    private static bool IsInteger(JsonElement element)
    {
        if (element.TryGetInt64(out _))
            return true;
        return element.TryGetDouble(out var value) && value == Math.Truncate(value) && !double.IsInfinity(value);
    }

    private static string DescribeType(JsonElement instance)
        => instance.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => IsInteger(instance) ? "integer" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => instance.ValueKind.ToString(),
        };
}
