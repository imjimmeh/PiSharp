using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PiSharp.Mcp.Tests;

public sealed class McpClientHostTests
{
    private static JsonElement ServersJson(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static string ServerConfigJson(string command, params (string Key, string Value)[] extra)
    {
        var parts = extra.Select(pair => $"\"{pair.Key}\":{pair.Value}").Prepend($"\"command\":\"{command}\"").Prepend("\"enabled\":true");
        return "{" + string.Join(",", parts) + "}";
    }

    [Fact]
    public async Task Reconcile_AddsAndRemovesServersFromSettings()
    {
        await using var mock = new MockMcpServer([TestMcp.Tool("read")]);
        var api = new TestExtensionApi();
        var host = new McpClientHost(api, TestMcp.Context(), TimeSpan.FromMilliseconds(10),
            kind => kind == McpTransportKind.Stdio ? new TestStreamTransportFactory(mock) : null);

        await host.StartAsync();
        Assert.Empty(await host.GetAllStatusAsync());

        await api.Settings.SetAsync("mcpServers", ServersJson("""{"fileserver":{"command":"mock-server","enabled":true}}"""));
        await WaitUntilAsync(() => host.GetAllStatusAsync().Result.Any(status => status.State == "connected"), TimeSpan.FromSeconds(5));
        var status = await host.GetStatusAsync("fileserver");
        Assert.Equal("connected", status.State);
        Assert.Equal(1, status.ToolCount);

        await api.Settings.SetAsync("mcpServers", ServersJson("{}"));
        await WaitUntilAsync(() => !host.GetAllStatusAsync().Result.Any(status => status.Name == "fileserver"), TimeSpan.FromSeconds(5));
        Assert.Equal("disconnected", (await host.GetStatusAsync("fileserver")).State);

        await host.StopAsync();
    }

    [Fact]
    public async Task AutoConnectFalse_DoesNotStartServers()
    {
        var api = new TestExtensionApi();
        await api.Settings.SetAsync("autoConnect", false);
        await api.Settings.SetAsync("mcpServers", ServersJson("""{"fileserver":{"command":"node","enabled":true}}"""));

        var host = new McpClientHost(api, TestMcp.Context(), TimeSpan.FromMilliseconds(10));
        await host.StartAsync();

        var all = await host.GetAllStatusAsync();
        var fileserver = Assert.Single(all);
        Assert.Equal("disconnected", fileserver.State);

        await host.StopAsync();
    }

    [Fact]
    public async Task InvalidConfig_ReportsPerServerErrorWithoutCrashing()
    {
        var api = new TestExtensionApi();
        await api.Settings.SetAsync("mcpServers", ServersJson("""{"broken":{"enabled":true}}"""));

        var host = new McpClientHost(api, TestMcp.Context(), TimeSpan.FromMilliseconds(10));
        await host.StartAsync();

        var status = await host.GetStatusAsync("broken");
        Assert.Equal("error", status.State);
        Assert.Contains("command", status.LastError, StringComparison.OrdinalIgnoreCase);

        await host.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Condition was not met within the timeout.");
    }
}
