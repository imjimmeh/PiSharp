using System.Text.Json;
using Xunit;

namespace PiSharp.Mcp.Tests;

public sealed class McpSlashCommandTests
{
    [Fact]
    public async Task List_ShowsConfiguredServers()
    {
        await using var mock = new MockMcpServer([TestMcp.Tool("read")]);
        var api = new TestExtensionApi();
        var host = new McpClientHost(api, TestMcp.Context(), TimeSpan.FromMilliseconds(10),
            kind => kind == McpTransportKind.Stdio ? new TestStreamTransportFactory(mock) : null);
        await host.StartAsync();

        var command = new McpSlashCommand(host, api);
        await command.HandleAsync("list", CancellationToken.None);

        var sent = api.SentMessages.Select(TestMessages.UserText).ToArray();
        Assert.Contains(sent, text => text.Contains("No MCP servers configured", StringComparison.Ordinal));

        await host.StopAsync();
    }

    [Fact]
    public async Task Status_UnknownServerReportsError()
    {
        var api = new TestExtensionApi();
        var host = new McpClientHost(api, TestMcp.Context(), TimeSpan.FromMilliseconds(10));
        await host.StartAsync();

        var command = new McpSlashCommand(host, api);
        await command.HandleAsync("status nosuch", CancellationToken.None);

        var sent = api.SentMessages.Select(TestMessages.UserText).ToArray();
        Assert.Contains(sent, text => text.Contains("Unknown MCP server 'nosuch'.", StringComparison.Ordinal));

        await host.StopAsync();
    }

    [Fact]
    public async Task Connect_StartsServerAndReportsTools()
    {
        await using var mock = new MockMcpServer([TestMcp.Tool("read")]);
        var api = new TestExtensionApi();
        await api.Settings.SetAsync("mcpServers",
            JsonSerializer.Deserialize<JsonElement>("""{"fileserver":{"command":"mock-server","enabled":true}}"""));
        var host = new McpClientHost(api, TestMcp.Context(), TimeSpan.FromMilliseconds(10),
            kind => kind == McpTransportKind.Stdio ? new TestStreamTransportFactory(mock) : null);
        await host.StartAsync();

        var sent = api.SentMessages.Select(TestMessages.UserText).ToArray();
        Assert.Contains(sent, text => text.Contains("Connected MCP server 'fileserver' (1 tools).", StringComparison.Ordinal));

        await host.StopAsync();
    }

    [Fact]
    public async Task RenderList_FormatsStatusTable()
    {
        var rendered = McpSlashCommand.RenderList(
        [
            new McpServerStatus("fileserver", "settings", "connected", ToolCount: 3),
            new McpServerStatus("weather", "settings", "error", LastError: "boom")
        ]);
        Assert.Contains("fileserver", rendered);
        Assert.Contains("connected", rendered);
        Assert.Contains("boom", rendered);
    }
}
