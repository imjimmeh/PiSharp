using System.ComponentModel;
using System.Text.Json;
using ToolSchemaFactory = PiSharp.Tools.ToolSchemas;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class ToolSchemaGenerationTests
{
    [Fact]
    public void FromTypeGeneratesCamelCaseRequiredOptionalAndDescriptionMetadata()
    {
        var schema = ToolSchemaFactory.FromType<SchemaInput>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("requiredName", out var requiredName));
        Assert.True(properties.TryGetProperty("optionalNumber", out var optionalNumber));
        Assert.True(properties.TryGetProperty("optionalFlag", out var optionalFlag));
        Assert.False(properties.TryGetProperty("RequiredName", out _));

        Assert.Equal("string", requiredName.GetProperty("type").GetString());
        Assert.Equal("Required name description", requiredName.GetProperty("description").GetString());
        AssertJsonTypeUnion(optionalNumber, "number", "null");
        Assert.Equal("Optional number description", optionalNumber.GetProperty("description").GetString());
        AssertJsonTypeUnion(optionalFlag, "boolean", "null");
        Assert.Equal("Optional flag description", optionalFlag.GetProperty("description").GetString());

        AssertRequired(schema, "requiredName");
        AssertNotRequired(schema, "optionalNumber");
        AssertNotRequired(schema, "optionalFlag");
    }

    [Fact]
    public void FromTypeGeneratesNestedRecordAndListInputSchemas()
    {
        var schema = ToolSchemaFactory.FromType<NestedListInput>();
        var itemsProperty = schema.GetProperty("properties").GetProperty("items");
        var itemSchema = itemsProperty.GetProperty("items");
        var itemProperties = itemSchema.GetProperty("properties");

        Assert.Equal("array", itemsProperty.GetProperty("type").GetString());
        Assert.Equal("Nested items description", itemsProperty.GetProperty("description").GetString());
        Assert.Equal("object", itemSchema.GetProperty("type").GetString());
        Assert.Equal("string", itemProperties.GetProperty("oldText").GetProperty("type").GetString());
        AssertJsonTypeUnion(itemProperties.GetProperty("enabled"), "boolean", "null");
        Assert.Equal("Old text description", itemProperties.GetProperty("oldText").GetProperty("description").GetString());
        Assert.Equal("Enabled description", itemProperties.GetProperty("enabled").GetProperty("description").GetString());
        AssertRequired(schema, "items");
        AssertRequired(itemSchema, "oldText");
        AssertNotRequired(itemSchema, "enabled");
    }

    [Fact]
    public void FromTypeProducesExpectedBashLikeSchemaSnapshot()
    {
        var schema = ToolSchemaFactory.FromType<BashLikeInput>();

        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"Bash command to execute\"},\"timeout\":{\"type\":[\"number\",\"null\"],\"default\":null,\"description\":\"Timeout in seconds (optional, no default timeout)\"}},\"required\":[\"command\"]}",
            schema.GetRawText());
    }

    [Fact]
    public void FromTypeGeneratesObjectSchemaForOpenTypesAndPreservesDescriptions()
    {
        var schema = ToolSchemaFactory.FromType<OpenTypesInput>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");

        // Verify each open type is an object schema, NOT a boolean true
        Assert.True(properties.TryGetProperty("element", out var element));
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("object", element.GetProperty("type").GetString());
        Assert.Equal("Required JsonElement description", element.GetProperty("description").GetString());

        Assert.True(properties.TryGetProperty("nullableElement", out var nullableElement));
        Assert.Equal(JsonValueKind.Object, nullableElement.ValueKind);
        Assert.Equal("Optional JsonElement description", nullableElement.GetProperty("description").GetString());
        // Verify nullable open type schema is either type: object, type: [object, null], or anyOf containing object
        var hasValidType = (nullableElement.TryGetProperty("type", out var nType) && (nType.ValueKind == JsonValueKind.String && nType.GetString() == "object" || nType.ValueKind == JsonValueKind.Array))
            || (nullableElement.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array);
        Assert.True(hasValidType, "nullableElement must declare a valid object type or anyOf union");

        Assert.True(properties.TryGetProperty("node", out var node));
        Assert.Equal(JsonValueKind.Object, node.ValueKind);
        Assert.Equal("object", node.GetProperty("type").GetString());
        Assert.Equal("JsonNode description", node.GetProperty("description").GetString());

        Assert.True(properties.TryGetProperty("obj", out var obj));
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        Assert.Equal("object", obj.GetProperty("type").GetString());
        Assert.Equal("Object description", obj.GetProperty("description").GetString());

        Assert.True(properties.TryGetProperty("document", out var document));
        Assert.Equal(JsonValueKind.Object, document.ValueKind);
        Assert.Equal("object", document.GetProperty("type").GetString());
    }

    [Fact]
    public void FromTypeGeneratesStringEnumSchemaForEnumTypes()
    {
        var schema = ToolSchemaFactory.FromType<EnumSchemaInput>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("mode", out var mode));
        Assert.Equal(JsonValueKind.Object, mode.ValueKind);
        Assert.Equal("string", mode.GetProperty("type").GetString());
        Assert.Equal("Operation mode", mode.GetProperty("description").GetString());

        var enumValues = mode.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["firstOption", "secondOption", "customAction"], enumValues);
    }

    private static void AssertJsonTypeUnion(JsonElement schema, params string[] expectedTypes)
    {
        var actual = schema.GetProperty("type").EnumerateArray().Select(type => type.GetString()!).ToArray();
        Assert.Equal(expectedTypes, actual);
    }

    private static void AssertRequired(JsonElement schema, string propertyName)
    {
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()!).ToArray();
        Assert.Contains(propertyName, required);
    }

    private static void AssertNotRequired(JsonElement schema, string propertyName)
    {
        if (!schema.TryGetProperty("required", out var required)) return;
        Assert.DoesNotContain(propertyName, required.EnumerateArray().Select(name => name.GetString()!).ToArray());
    }

    private sealed record SchemaInput(
        [property: Description("Required name description")]
        string RequiredName,

        [property: Description("Optional number description")]
        double? OptionalNumber = null,

        [Description("Optional flag description")]
        bool? OptionalFlag = null);

    private sealed record NestedListInput(
        [property: Description("Nested items description")]
        IReadOnlyList<NestedItemInput> Items);

    private sealed record NestedItemInput(
        [property: Description("Old text description")]
        string OldText,

        [property: Description("Enabled description")]
        bool? Enabled = null);

    private sealed record BashLikeInput(
        [property: Description("Bash command to execute")]
        string Command,

        [property: Description("Timeout in seconds (optional, no default timeout)")]
        double? Timeout = null);

    private sealed record OpenTypesInput(
        [property: Description("Required JsonElement description")]
        JsonElement Element,

        [property: Description("Optional JsonElement description")]
        JsonElement? NullableElement = null,

        [property: Description("JsonNode description")]
        System.Text.Json.Nodes.JsonNode? Node = null,

        [property: Description("Object description")]
        object? Obj = null,

        [property: Description("Document description")]
        JsonDocument? Document = null);

    private enum TestMode
    {
        FirstOption,
        SecondOption,
        CustomAction
    }

    private sealed record EnumSchemaInput(
        [property: Description("Operation mode")]
        TestMode Mode);
}
