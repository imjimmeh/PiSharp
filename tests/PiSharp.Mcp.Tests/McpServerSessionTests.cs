using PiSharp.Abstractions.Messages;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Mcp.Tests;

public sealed class McpServerSessionTests
{
    [Fact]
    public async Task ConnectAsync_RegistersToolsAndCallsThrough()
    {
        await using var mock = new MockMcpServer(
            [TestMcp.Tool("read", "Read a file."), TestMcp.Tool("write")],
            (parameters, _) => Task.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"echo {parameters.Arguments?["text"]}" }]
            }));

        var api = new TestExtensionApi();
        var session = new McpServerSession(
            TestMcp.StdioServer(),
            TestMcp.Context(),
            api,
            new McpHostOptions(ToolPrefix: "mcp"),
            kind => kind == McpTransportKind.Stdio ? new TestStreamTransportFactory(mock) : null);

        await session.ConnectAsync(CancellationToken.None);

        Assert.Equal("connected", session.GetStatus().State);
        Assert.Equal(2, session.GetStatus().ToolCount);
        var registered = api.Registry.Tools.Select(owned => owned.Value).First(tool => tool.Name == "mcp.fileserver.read");
        var parameters = JsonSerializer.Deserialize<JsonElement>("""{"text":"hi"}""");
        var result = await registered.ExecuteAsync("id", parameters);

        Assert.Equal("echo hi", ((TextContent)result.Content[0]).Text);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_ToolNameCollisionBecomesErrorState()
    {
        await using var mock = new MockMcpServer([TestMcp.Tool("read")]);

        var api = new TestExtensionApi();
        // Squat the mcp.* namespace with a Reject override policy.
        using var squatter = api.RegisterTool(new ExtensionToolRegistration(
            Name: "mcp.fileserver.read",
            Label: "squatter",
            Description: "someone else",
            ParametersSchema: JsonSerializer.Deserialize<JsonElement>("{}"),
            ExecuteAsync: (_, _, _, _) => Task.FromResult(new AgentToolResult<object?>([], null))));

        var session = new McpServerSession(
            TestMcp.StdioServer(),
            TestMcp.Context(),
            api,
            new McpHostOptions(ToolPrefix: "mcp"),
            kind => kind == McpTransportKind.Stdio ? new TestStreamTransportFactory(mock) : null);

        await session.ConnectAsync(CancellationToken.None);

        Assert.Equal("error", session.GetStatus().State);
        Assert.Contains("registration failed", session.GetStatus().LastError, StringComparison.OrdinalIgnoreCase);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_UnknownTransportKindBecomesErrorState()
    {
        var api = new TestExtensionApi();
        var session = new McpServerSession(
            TestMcp.HttpServer(),
            TestMcp.Context(),
            api,
            new McpHostOptions(),
            kind => null);

        await session.ConnectAsync(CancellationToken.None);
        Assert.Equal("error", session.GetStatus().State);
        Assert.Contains("transport factory", session.GetStatus().LastError, StringComparison.OrdinalIgnoreCase);
        await session.DisposeAsync();
    }
}
