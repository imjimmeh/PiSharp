using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Subagents.AgentDefinitions;
using PiSharp.Subagents.Discovery;
using PiSharp.Subagents.Spawning;
using PiSharp.Subagents.Tools;
using Xunit;

namespace PiSharp.Subagents.Tests;

public sealed class YieldToolTests
{
    private static JsonElement ObjectArgs(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task YieldReturnsDataAndTerminatesWhenConforming()
    {
        var tool = new YieldTool();

        var result = await tool.ExecuteAsync("call-1", ObjectArgs("""{"data":{"answer":42}}"""), CancellationToken.None);

        Assert.True(result.Terminate);
        var details = Assert.IsType<JsonElement>(result.Details);
        Assert.Equal(JsonValueKind.Object, details.ValueKind);
        Assert.Equal(42, details.GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task YieldRejectsNonConformingOutputWithoutTerminating()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","required":["findings"],"properties":{"findings":{"type":"array","minItems":1}}}
            """).RootElement.Clone();
        var tool = new YieldTool(schema);

        var result = await tool.ExecuteAsync("call-1", ObjectArgs("""{"data":{"nope":true}}"""), CancellationToken.None);

        Assert.False(result.Terminate);
        Assert.Null(result.Details);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("yield rejected", text, StringComparison.Ordinal);
        Assert.Contains("findings", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YieldRejectsMalformedArguments()
    {
        var tool = new YieldTool();

        var result = await tool.ExecuteAsync("call-1", ObjectArgs("""{"wrong":"shape"}"""), CancellationToken.None);

        Assert.False(result.Terminate);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("yield requires a JSON object", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YieldReportsExactErrorPathForNestedViolation()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"findings":{"type":"array","items":{"type":"object","required":["summary"],"properties":{"summary":{"type":"string"}}}}}}
            """).RootElement.Clone();
        var tool = new YieldTool(schema);

        var result = await tool.ExecuteAsync(
            "call-1",
            ObjectArgs("""{"data":{"findings":[{"severity":"high"}]}}"""),
            CancellationToken.None);

        Assert.False(result.Terminate);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("$.findings[0]", text, StringComparison.Ordinal);
    }
}

public sealed class TaskToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task TaskToolSurfacesBlockedSpawnAsReadableErrorResult()
    {
        var registry = new AgentDefinitionRegistry();
        registry.Replace(new Dictionary<string, AgentDefinition>(StringComparer.Ordinal));
        var coordinator = new SubagentSpawnCoordinator(registry, SubagentSettings.Default);
        var tool = new TaskTool(coordinator);

        var result = await tool.ExecuteAsync("call-1", Args("""{"agent":"ghost","task":"x"}"""), CancellationToken.None);

        Assert.False(result.Terminate);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("task blocked", text, StringComparison.Ordinal);
        Assert.Contains("unknown-agent", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskToolRejectsMalformedArguments()
    {
        var coordinator = new SubagentSpawnCoordinator(new AgentDefinitionRegistry(), SubagentSettings.Default);
        var tool = new TaskTool(coordinator);

        var result = await tool.ExecuteAsync("call-1", Args("""{"nope":true}"""), CancellationToken.None);

        Assert.False(result.Terminate);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("task requires a JSON object", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskToolExposesSerializableParameterSchema()
    {
        var coordinator = new SubagentSpawnCoordinator(new AgentDefinitionRegistry(), SubagentSettings.Default);
        var tool = new TaskTool(coordinator);

        var schema = tool.ParametersSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("agent", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("task", out _));
    }
}
