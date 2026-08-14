using System.Text.Json;
using Xunit;

namespace PiSharp.DeclarativeTools.Tests;

public sealed class ToolSchemaBuilderTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Build_EmptyFragment_ProducesEmptyObjectSchema()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(Parse("{}"), [], additionalProperties: false);

        Assert.Null(diagnostic);
        Assert.NotNull(schema);
        using var doc = JsonDocument.Parse(schema!.Value.GetRawText());
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("properties").EnumerateObject().Count());
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("required", out _));
    }

    [Fact]
    public void Build_PropertiesAndRequired_ProduceObjectShape()
    {
        var fragment = Parse("""
            { "query": { "type": "string", "description": "q" }, "limit": { "type": "integer", "enum": [1, 2] } }
            """);
        var (schema, diagnostic) = ToolSchemaBuilder.Build(fragment, ["query"], additionalProperties: false);

        Assert.Null(diagnostic);
        using var doc = JsonDocument.Parse(schema!.Value.GetRawText());
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        var properties = doc.RootElement.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("q", properties.GetProperty("query").GetProperty("description").GetString());
        Assert.Equal("integer", properties.GetProperty("limit").GetProperty("type").GetString());
        Assert.Equal(1, properties.GetProperty("limit").GetProperty("enum")[0].GetInt32());
        Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("query", doc.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void Build_AdditionalPropertiesTrue_IsForwarded()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(Parse("{}"), [], additionalProperties: true);

        Assert.Null(diagnostic);
        using var doc = JsonDocument.Parse(schema!.Value.GetRawText());
        Assert.True(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Build_PassThroughKeys_AreForwardedVerbatim()
    {
        var fragment = Parse("""{ "q": { "type": "string", "minLength": 2, "x-custom": "kept" } }""");
        var (schema, diagnostic) = ToolSchemaBuilder.Build(fragment, [], additionalProperties: false);

        Assert.Null(diagnostic);
        using var doc = JsonDocument.Parse(schema!.Value.GetRawText());
        var node = doc.RootElement.GetProperty("properties").GetProperty("q");
        Assert.Equal(2, node.GetProperty("minLength").GetInt32());
        Assert.Equal("kept", node.GetProperty("x-custom").GetString());
    }

    [Fact]
    public void Build_NonObjectFragment_IsDiagnostic()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(Parse("\"nope\""), [], additionalProperties: false);
        Assert.Null(schema);
        Assert.Contains("JSON object", diagnostic);
    }

    [Fact]
    public void Build_NodeWithoutType_IsDiagnostic()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(Parse("""{ "q": { "description": "x" } }"""), [], additionalProperties: false);
        Assert.Null(schema);
        Assert.Contains("missing a 'type'", diagnostic);
    }

    [Fact]
    public void Build_UnknownType_IsDiagnostic()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(Parse("""{ "q": { "type": "banana" } }"""), [], additionalProperties: false);
        Assert.Null(schema);
        Assert.Contains("banana", diagnostic);
    }

    [Fact]
    public void Build_NonObjectNode_IsDiagnostic()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(Parse("""{ "q": "string" }"""), [], additionalProperties: false);
        Assert.Null(schema);
        Assert.Contains("schema object", diagnostic);
    }

    [Fact]
    public void Build_NullParameters_ProducesNoSchema()
    {
        var (schema, diagnostic) = ToolSchemaBuilder.Build(null, [], additionalProperties: false);
        Assert.Null(schema);
        Assert.Null(diagnostic);
    }
}
