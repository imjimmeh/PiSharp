using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PiSharp.Agent.Core.Tools;
using Xunit;

namespace PiSharp.Mcp.Tests;

public sealed class McpToolAdapterTests
{
    private static readonly JsonElement EmptySchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");

    /// <summary>
    /// Builds an SDK tool wrapper for adapter tests. <see cref="McpClientTool"/>'s constructor
    /// requires a live <see cref="ModelContextProtocol.Client.McpClient"/>, which is unnecessary
    /// here — <see cref="McpToolAdapter.ToRegistration"/> only reads <see cref="McpClientTool.ProtocolTool"/>.
    /// </summary>
    private static McpClientTool ClientTool(string name, string? description = null)
    {
        var clientTool = (McpClientTool)RuntimeHelpers.GetUninitializedObject(typeof(McpClientTool));
        typeof(McpClientTool)
            .GetField("<ProtocolTool>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(clientTool, TestMcp.Tool(name, description));
        return clientTool;
    }

    [Fact]
    public void ToRegistration_MapsNameLabelAndDescription()
    {
        var adapter = new McpToolAdapter("fileserver", "mcp", null, (_, _, _) => Task.FromResult(new CallToolResult()));
        var registration = adapter.ToRegistration(ClientTool("read", "Read a file."));

        Assert.Equal("mcp.fileserver.read", registration.Name);
        Assert.Equal("fileserver.read", registration.Label);
        Assert.Equal("Read a file.", registration.Description);
    }

    [Fact]
    public void ToRegistration_FallsBackDescriptionWhenMissing()
    {
        var adapter = new McpToolAdapter("fileserver", "mcp", null, (_, _, _) => Task.FromResult(new CallToolResult()));
        var registration = adapter.ToRegistration(ClientTool("read", null));
        Assert.Equal("MCP tool 'fileserver.read'.", registration.Description);
    }

    [Fact]
    public void ExecuteAsync_ForwardsArgumentsAndFormatsResult()
    {
        var captured = new Dictionary<string, IReadOnlyDictionary<string, object?>?>();
        var adapter = new McpToolAdapter("fileserver", "mcp", null, (toolName, arguments, _) =>
        {
            captured[toolName] = arguments;
            return Task.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "hello world" }]
            });
        });

        var registration = adapter.ToRegistration(ClientTool("read"));
        var parameters = JsonSerializer.Deserialize<JsonElement>("""{"path":"/tmp/a.txt"}""");
        var result = registration.ExecuteAsync("id", parameters, CancellationToken.None, null).GetAwaiter().GetResult();

        Assert.NotNull(captured["read"]);
        Assert.Equal("hello world", ((TextContent)result.Content[0]).Text);
    }
}
