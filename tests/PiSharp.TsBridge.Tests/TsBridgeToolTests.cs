using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class TsBridgeToolTests
{
    [Fact]
    public void ToolDefinitionPreservesSchemaAndExecutionMode()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var definition = new TsToolDefinition("echo", "Echo", "Echo input", schema.RootElement.Clone(), ToolExecutionMode.Sequential);
        Assert.Equal("echo", definition.Name);
        Assert.Equal(ToolExecutionMode.Sequential, definition.ExecutionMode);
    }
}
