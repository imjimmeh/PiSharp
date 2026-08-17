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
    private static readonly JsonSerializerOptions SchemaSerializerOptions = CreateSchemaSerializerOptions();

    private static JsonSerializerOptions CreateSchemaSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static readonly JsonSchemaExporterOptions SchemaExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = TransformSchemaNode
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

        SanitizeSchema(schemaObject, type);
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

    private static JsonNode TransformSchemaNode(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is JsonValue value && value.TryGetValue<bool>(out var boolVal) && boolVal)
        {
            schema = new JsonObject
            {
                ["type"] = "object"
            };
        }
        else if (schema is JsonObject obj)
        {
            if (obj.ContainsKey("enum") && !obj.ContainsKey("type"))
            {
                obj["type"] = "string";
            }
            else if (!obj.ContainsKey("type") && !obj.ContainsKey("$ref") && !obj.ContainsKey("anyOf") && !obj.ContainsKey("oneOf") && !obj.ContainsKey("allOf"))
            {
                obj["type"] = "object";
            }
        }

        var description = GetDescription(context);
        if (description is not null && schema is JsonObject schemaObject)
        {
            schemaObject["description"] = description;
        }

        return schema;
    }

    public static void SanitizeSchema(JsonObject schemaObject, Type? rootType = null)
    {
        SanitizeNode(schemaObject);

        if (rootType is not null && schemaObject.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject propsObj)
        {
            var props = rootType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var ctors = rootType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var (propName, propSchemaNode) in propsObj.ToList())
            {
                if (propSchemaNode is JsonObject propObj && !propObj.ContainsKey("description"))
                {
                    var prop = props.FirstOrDefault(p => string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase));
                    var desc = GetDescription(prop);

                    if (desc is null)
                    {
                        foreach (var ctor in ctors)
                        {
                            var param = ctor.GetParameters().FirstOrDefault(p => string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase));
                            desc = GetDescription(param);
                            if (desc is not null) break;
                        }
                    }

                    if (desc is not null)
                    {
                        propObj["description"] = desc;
                    }
                }
            }
        }
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

            if (!obj.ContainsKey("description"))
            {
                if (obj.TryGetPropertyValue("anyOf", out var anyOfNode) && anyOfNode is JsonArray anyOfArr)
                {
                    foreach (var branch in anyOfArr)
                    {
                        if (branch is JsonObject branchObj && branchObj.TryGetPropertyValue("description", out var desc) && desc is not null)
                        {
                            obj["description"] = desc.DeepClone();
                            break;
                        }
                    }
                }
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

    private static string? GetDescription(JsonSchemaExporterContext context)
    {
        if (context.PropertyInfo is null) return null;

        var desc = GetDescription(context.PropertyInfo.AttributeProvider)
            ?? GetDescription(context.PropertyInfo.AssociatedParameter?.AttributeProvider);

        if (desc is not null) return desc;

        if (context.PropertyInfo.DeclaringType is { } declaringType)
        {
            var prop = declaringType.GetProperty(context.PropertyInfo.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            desc = GetDescription(prop);
            if (desc is not null) return desc;

            foreach (var ctor in declaringType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var param = ctor.GetParameters().FirstOrDefault(p => string.Equals(p.Name, context.PropertyInfo.Name, StringComparison.OrdinalIgnoreCase));
                desc = GetDescription(param);
                if (desc is not null) return desc;
            }
        }

        return null;
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
