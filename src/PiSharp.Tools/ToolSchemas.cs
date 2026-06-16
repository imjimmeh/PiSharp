using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PiSharp.Tools;

public static class ToolSchemas
{
    private static readonly JsonSerializerOptions SchemaSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private static readonly JsonSchemaExporterOptions SchemaExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = AddDescriptionMetadata
    };

    public static JsonElement FromType<T>() => FromType(typeof(T));

    public static JsonElement FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(SchemaSerializerOptions, type, SchemaExporterOptions);
        if (schema is not JsonObject schemaObject)
        {
            throw new InvalidOperationException($"Generated schema for '{type}' is not a JSON object.");
        }

        EnsureProviderCompatibleRoot(type, schemaObject);
        using var document = JsonDocument.Parse(schemaObject.ToJsonString());
        return document.RootElement.Clone();
    }

    public static JsonElement Object(IReadOnlyDictionary<string, JsonElement> properties, IReadOnlyCollection<string>? required = null, bool additionalProperties = false)
        => Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            foreach (var (name, schema) in properties)
            {
                writer.WritePropertyName(name);
                schema.WriteTo(writer);
            }
            writer.WriteEndObject();
            if (required is { Count: > 0 })
            {
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                foreach (var name in required) writer.WriteStringValue(name);
                writer.WriteEndArray();
            }
            writer.WriteBoolean("additionalProperties", additionalProperties);
            writer.WriteEndObject();
        });

    public static JsonElement String(string description) => Typed("string", description);
    public static JsonElement Number(string description) => Typed("number", description);
    public static JsonElement Boolean(string description) => Typed("boolean", description);

    public static JsonElement Array(JsonElement items, string description)
        => Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "array");
            writer.WriteString("description", description);
            writer.WritePropertyName("items");
            items.WriteTo(writer);
            writer.WriteEndObject();
        });

    private static JsonElement Typed(string type, string description)
        => Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("description", description);
            writer.WriteEndObject();
        });

    private static JsonNode AddDescriptionMetadata(JsonSchemaExporterContext context, JsonNode schema)
    {
        var description = GetDescription(context.PropertyInfo?.AttributeProvider)
            ?? GetDescription(context.PropertyInfo?.AssociatedParameter?.AttributeProvider);
        if (description is not null && schema is JsonObject schemaObject)
        {
            schemaObject["description"] = description;
        }

        return schema;
    }

    private static string? GetDescription(ICustomAttributeProvider? attributeProvider)
        => attributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description;

    private static void EnsureProviderCompatibleRoot(Type type, JsonObject schemaObject)
    {
        if (!schemaObject.TryGetPropertyValue("type", out var typeNode))
        {
            throw new InvalidOperationException($"Generated schema for '{type}' does not declare a root type.");
        }

        if (typeNode is JsonValue typeValue && typeValue.TryGetValue<string>(out var schemaType) && schemaType == "object")
        {
            return;
        }

        if (typeNode is JsonArray typeArray && typeArray.Any(IsObjectType))
        {
            schemaObject["type"] = "object";
            return;
        }

        throw new InvalidOperationException($"Generated schema for '{type}' is not a root object schema.");
    }

    private static bool IsObjectType(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var schemaType) && schemaType == "object";

    private static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
