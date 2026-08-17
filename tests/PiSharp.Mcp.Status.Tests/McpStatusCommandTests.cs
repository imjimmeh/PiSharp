using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Mcp;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Mcp.Status.Tests;

/// <summary>
/// Tests for the <c>mcp_status</c> daemon command (plan §6 C2): the read-only wire surface that
/// returns structured MCP server statuses for future SDK clients (P22) and TUI status panels.
/// </summary>
public sealed class McpStatusCommandTests
{
    [Fact]
    public void CommandConstant_IsMcpStatus()
    {
        Assert.Equal("mcp_status", ServerCommandTypes.McpStatus);
    }

    [Fact]
    public async Task McpStatusCommand_WithoutDelegate_ReturnsNotAvailable()
    {
        var handler = CreateHandler(delegates: null);

        var response = await handler.DispatchTextCommandAsync(
            JsonSerializer.Serialize(new { id = "1", type = ServerCommandTypes.McpStatus }, ServerJsonSerializer.Options));

        Assert.False(response.Success);
        Assert.Equal("not_available", response.Error?.Code);
        Assert.Equal(ServerCommandTypes.McpStatus, response.Command);
    }

    [Fact]
    public async Task McpStatusCommand_WithDelegate_ReturnsServerStatuses()
    {
        var expected = new McpStatusResult(
        [
            new McpServerStatusEntry("filesystem", "settings", "connected", ToolCount: 3, ServerInfo: "filesystem 0.1.0"),
            new McpServerStatusEntry("linear", "settings", "error", LastError: "auth required"),
        ]);

        var handler = CreateHandler(delegates: new PiServerCommandDelegates(
            GetMcpStatusAsync: _ => Task.FromResult(expected)));

        var response = await handler.DispatchTextCommandAsync(
            JsonSerializer.Serialize(new { id = "2", type = ServerCommandTypes.McpStatus }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        Assert.Equal(ServerCommandTypes.McpStatus, response.Command);
        var result = Assert.IsType<McpStatusResult>(response.Data);
        Assert.Equal(2, result.Servers.Count);

        var first = result.Servers[0];
        Assert.Equal("filesystem", first.Name);
        Assert.Equal("settings", first.Source);
        Assert.Equal("connected", first.State);
        Assert.Equal(3, first.ToolCount);
        Assert.Equal("filesystem 0.1.0", first.ServerInfo);

        var second = result.Servers[1];
        Assert.Equal("linear", second.Name);
        Assert.Equal("error", second.State);
        Assert.Equal("auth required", second.LastError);
    }

    [Fact]
    public async Task McpStatusCommand_WithEmptyServerList_ReturnsEmptyResult()
    {
        var handler = CreateHandler(delegates: new PiServerCommandDelegates(
            GetMcpStatusAsync: _ => Task.FromResult(new McpStatusResult([]))));

        var response = await handler.DispatchTextCommandAsync(
            JsonSerializer.Serialize(new { id = "3", type = ServerCommandTypes.McpStatus }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
        var result = Assert.IsType<McpStatusResult>(response.Data);
        Assert.Empty(result.Servers);
    }

    [Fact]
    public async Task McpStatusCommand_DoesNotRequireServerSessionId()
    {
        // mcp_status is a read-only, session-independent command (plan §6 C2: "outside RunExclusiveAsync —
        // it only reads plugin state"). It must succeed without a serverSessionId in the envelope.
        var handler = CreateHandler(delegates: new PiServerCommandDelegates(
            GetMcpStatusAsync: _ => Task.FromResult(new McpStatusResult([]))));

        var response = await handler.DispatchTextCommandAsync(
            JsonSerializer.Serialize(new { id = "4", type = ServerCommandTypes.McpStatus }, ServerJsonSerializer.Options));

        Assert.True(response.Success);
    }

    [Fact]
    public void McpServerStatusEntry_MirrorsPluginMcpServerStatus()
    {
        // The server-side entry must carry the same fields as PiSharp.Mcp.McpServerStatus so the
        // delegate can map one-to-one without the server referencing the plugin assembly.
        var plugin = new McpServerStatus("fs", "settings", "connected", 5, null, "fs 1.0", 2);
        var entry = new McpServerStatusEntry(plugin.Name, plugin.Source, plugin.State, plugin.ToolCount, plugin.LastError, plugin.ServerInfo, plugin.ReconnectAttempt);

        Assert.Equal(plugin.Name, entry.Name);
        Assert.Equal(plugin.Source, entry.Source);
        Assert.Equal(plugin.State, entry.State);
        Assert.Equal(plugin.ToolCount, entry.ToolCount);
        Assert.Equal(plugin.LastError, entry.LastError);
        Assert.Equal(plugin.ServerInfo, entry.ServerInfo);
        Assert.Equal(plugin.ReconnectAttempt, entry.ReconnectAttempt);
    }

    private static PiServerWebSocketHandler CreateHandler(PiServerCommandDelegates? delegates = null)
        => new(
            new ServerSessionRegistry((request, _) => Task.FromException<PiSharp.Server.Runtime.SessionRuntimeResult>(new InvalidOperationException("No session should be created for mcp_status tests."))),
            new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }),
            NullLogger<PiServerWebSocketHandler>.Instance,
            uiBridge: null,
            delegates);
}
