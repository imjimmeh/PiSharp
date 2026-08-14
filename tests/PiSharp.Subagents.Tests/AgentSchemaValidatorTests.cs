using System.Text.Json;
using PiSharp.Subagents.Validation;
using Xunit;

namespace PiSharp.Subagents.Tests;

public sealed class AgentSchemaValidatorTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static JsonElement Instance(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ValidateAcceptsConformingObject()
    {
        var schema = Schema("""
            {
              "type": "object",
              "required": ["findings"],
              "properties": {
                "findings": {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "required": ["severity", "summary"],
                    "properties": {
                      "severity": { "type": "string", "enum": ["critical", "warning", "info"] },
                      "summary": { "type": "string" }
                    },
                    "additionalProperties": false
                  }
                }
              }
            }
            """);
        var instance = Instance("""{"findings":[{"severity":"warning","summary":"ok"}]}""");

        Assert.True(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateAcceptsAnythingWhenSchemaIsNull()
    {
        var instance = Instance("""{"anything": 42}""");

        Assert.True(AgentSchemaValidator.Validate(null, instance, out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRejectsMissingRequiredProperty()
    {
        var schema = Schema("""{"type":"object","required":["findings"],"properties":{"findings":{"type":"array"}}}""");
        var instance = Instance("""{"summary":"missing findings"}""");

        Assert.False(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Contains(errors, error => error.Contains("missing required property 'findings'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsWrongType()
    {
        var schema = Schema("""{"type":"object","required":["findings"],"properties":{"findings":{"type":"array"}}}""");
        var instance = Instance("""{"findings":"not-an-array"}""");

        Assert.False(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Contains(errors, error => error.Contains("$.findings: expected type 'array'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsBadEnumValue()
    {
        var schema = Schema("""{"type":"object","properties":{"severity":{"type":"string","enum":["critical","warning"]}}}""");
        var instance = Instance("""{"severity":"fatal"}""");

        Assert.False(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Contains(errors, error => error.Contains("not one of the allowed enum values", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsArrayBelowMinItems()
    {
        var schema = Schema("""{"type":"object","properties":{"findings":{"type":"array","minItems":1}}}""");
        var instance = Instance("""{"findings":[]}""");

        Assert.False(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Contains(errors, error => error.Contains("expected at least 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsUndeclaredPropertiesWhenAdditionalPropertiesFalse()
    {
        var schema = Schema("""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}""");
        var instance = Instance("""{"a":"x","b":"extra"}""");

        Assert.False(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Contains(errors, error => error.Contains("additional property 'b' is not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateReportsNestedItemPath()
    {
        var schema = Schema("""
            {"type":"object","properties":{"findings":{"type":"array","items":{"type":"object","required":["summary"],"properties":{"summary":{"type":"string"}}}}}}
            """);
        var instance = Instance("""{"findings":[{"severity":"warning"},{"summary":"ok"}]}""");

        Assert.False(AgentSchemaValidator.Validate(schema, instance, out var errors));
        Assert.Contains(errors, error => error.Contains("$.findings[0]: missing required property 'summary'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsIntegerForIntegerType()
    {
        var schema = Schema("""{"type":"integer"}""");
        Assert.True(AgentSchemaValidator.Validate(schema, Instance("42"), out _));
        Assert.True(AgentSchemaValidator.Validate(schema, Instance("42.0"), out _));
        Assert.False(AgentSchemaValidator.Validate(schema, Instance("42.5"), out _));
    }
}
