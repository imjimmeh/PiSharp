using System.Text.Json;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class BuiltInToolsTests
{
    [Fact]
    public void CreateAllReturnsAllSevenBuiltInTools()
    {
        var tools = BuiltInTools.CreateAll(new FakeExecutionEnv());
        Assert.Equal(["bash", "edit", "find", "grep", "ls", "read", "write"], tools.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void CreateReadOnlyOmitsMutationAndShellTools()
    {
        var tools = BuiltInTools.CreateReadOnly(new FakeExecutionEnv());
        Assert.Equal(["find", "grep", "ls", "read"], tools.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("bash", tools.Keys);
        Assert.DoesNotContain("edit", tools.Keys);
        Assert.DoesNotContain("write", tools.Keys);
    }

    [Fact]
    public void CreateToolRejectsUnknownNames()
    {
        Assert.Throws<ArgumentException>(() => BuiltInTools.CreateTool("unknown", new FakeExecutionEnv()));
    }

    [Fact]
    public void ToolsExposeTypeScriptCompatibleSchemas()
    {
        var tools = BuiltInTools.CreateAll(new FakeExecutionEnv());
        Assert.Equal(7, tools.Count);

        AssertSchemaProperties(tools["bash"].ParametersSchema, "command", "timeout");
        AssertSchemaProperties(tools["read"].ParametersSchema, "path", "offset", "limit");
        AssertSchemaProperties(tools["write"].ParametersSchema, "path", "content");
        AssertSchemaProperties(tools["find"].ParametersSchema, "pattern", "path", "limit");
        AssertSchemaProperties(tools["grep"].ParametersSchema, "pattern", "path", "glob", "ignoreCase", "literal", "context", "limit");
        AssertSchemaProperties(tools["ls"].ParametersSchema, "path", "limit");

        var editSchema = tools["edit"].ParametersSchema;
        AssertSchemaProperties(editSchema, "path", "edits");
        AssertSchemaProperties(editSchema.GetProperty("properties").GetProperty("edits").GetProperty("items"), "oldText", "newText");

        var combined = string.Concat(tools.Values.Select(tool => tool.ParametersSchema.GetRawText()));
        Assert.DoesNotContain("filePath", combined);
        Assert.DoesNotContain("oldString", combined);
        Assert.DoesNotContain("maxResults", combined);
    }

    private static void AssertSchemaProperties(JsonElement schema, params string[] expectedProperties)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        foreach (var property in expectedProperties)
        {
            Assert.True(properties.TryGetProperty(property, out _), $"Expected schema to contain property '{property}'.");
        }
    }
}
