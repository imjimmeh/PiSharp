using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Tools;

namespace PiSharp.DeclarativeTools;

/// <summary>
/// Validates the <c>parameters</c> frontmatter/JSON fragment and builds the final
/// parameters schema with the shared <see cref="ToolSchemas.Object"/> helper
/// (plan §5.1.4). A fragment is a JSON object mapping property names to schema
/// nodes; each node must declare a <c>type</c> from the allowed set and may carry
/// <c>description</c>/<c>items</c>/<c>enum</c> plus any pass-through keys.
/// </summary>
public static class ToolSchemaBuilder
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "string", "number", "boolean", "array", "object", "null", "integer"
    };

    /// <summary>
    /// Builds the final schema, or returns a per-file diagnostic for an invalid fragment.
    /// </summary>
    public static (JsonElement? Schema, string? Diagnostic) Build(
        JsonElement? parameters,
        IReadOnlyList<string> required,
        bool additionalProperties)
    {
        if (parameters is not { } fragment) return (null, null);

        if (fragment.ValueKind != JsonValueKind.Object)
            return (null, "'parameters' must be a JSON object mapping property names to schema nodes.");

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in fragment.EnumerateObject())
        {
            var diagnostic = ValidateNode(property.Name, property.Value);
            if (diagnostic is not null) return (null, diagnostic);
            properties[property.Name] = property.Value;
        }

        return (ToolSchemas.Object(properties, required, additionalProperties), null);
    }

    private static string? ValidateNode(string name, JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return $"Parameter '{name}' must be a schema object.";

        if (!node.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            return $"Parameter '{name}' is missing a 'type'.";
        var type = typeElement.GetString();
        if (type is null || !AllowedTypes.Contains(type))
            return $"Parameter '{name}' has unknown type '{type ?? "(null)"}'.";

        return null;
    }
}
