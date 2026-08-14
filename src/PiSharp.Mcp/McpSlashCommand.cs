using System.Text;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

namespace PiSharp.Mcp;

/// <summary>
/// The <c>/mcp</c> slash command. Subcommands: <c>list</c>, <c>status &lt;name&gt;</c>,
/// <c>connect &lt;name&gt;</c>, <c>disconnect &lt;name&gt;</c>, <c>reload</c>,
/// <c>login &lt;name&gt;</c>, <c>logout &lt;name&gt;</c>. Output is a plain-text table delivered as
/// a user message. The extension-command channel cannot carry an IsError flag (the CLI's
/// <c>SlashCommandRegistry</c> hardcodes the result), so errors are reported as
/// <c>[mcp] error: &lt;message&gt;</c> text instead.
/// </summary>
public sealed class McpSlashCommand
{
    private readonly McpClientHost _host;
    private readonly IExtensionApi _api;

    public McpSlashCommand(McpClientHost host, IExtensionApi api)
    {
        _host = host;
        _api = api;
    }

    public async Task HandleAsync(string args, CancellationToken cancellationToken = default)
    {
        var parts = string.IsNullOrWhiteSpace(args) ? [] : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        var name = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "list":
                await SendAsync(RenderList(await _host.GetAllStatusAsync(cancellationToken)), false, cancellationToken);
                break;
            case "status":
                if (name.Length == 0) { await SendErrorAsync("Usage: /mcp status <server>", cancellationToken); break; }
                await SendAsync(RenderStatus(await _host.GetStatusAsync(name, cancellationToken)), false, cancellationToken);
                break;
            case "connect":
                if (name.Length == 0) { await SendErrorAsync("Usage: /mcp connect <server>", cancellationToken); break; }
                await SendAsync(RenderConnectResult(await _host.ConnectAsync(name, cancellationToken), name, await _host.GetStatusAsync(name, cancellationToken)), false, cancellationToken);
                break;
            case "disconnect":
                if (name.Length == 0) { await SendErrorAsync("Usage: /mcp disconnect <server>", cancellationToken); break; }
                await SendAsync(RenderDisconnectResult(await _host.DisconnectAsync(name, cancellationToken), name), false, cancellationToken);
                break;
            case "reload":
                await _host.ReconcileAsync(cancellationToken);
                await SendAsync("MCP configuration reloaded.", false, cancellationToken);
                break;
            case "login":
                if (name.Length == 0) { await SendErrorAsync("Usage: /mcp login <server>", cancellationToken); break; }
                var login = await _host.LoginAsync(name, cancellationToken);
                await SendAsync(login.Message, !login.Success, cancellationToken);
                break;
            case "logout":
                if (name.Length == 0) { await SendErrorAsync("Usage: /mcp logout <server>", cancellationToken); break; }
                var logout = await _host.LogoutAsync(name, cancellationToken);
                await SendAsync(logout.Message, !logout.Success, cancellationToken);
                break;
            case "help":
                await SendAsync(RenderHelp(), false, cancellationToken);
                break;
            default:
                await SendErrorAsync($"Unknown /mcp subcommand '{command}'. Use '/mcp help' for usage.", cancellationToken);
                break;
        }
    }

    public static string RenderList(IReadOnlyList<McpServerStatus> statuses)
    {
        if (statuses.Count == 0) return "No MCP servers configured.";
        var builder = new StringBuilder();
        builder.AppendLine("MCP servers:");
        foreach (var status in statuses)
        {
            var tools = status.State == "connected" ? status.ToolCount.ToString() : "-";
            var error = status.LastError is null ? string.Empty : $"  ({status.LastError})";
            builder.AppendLine($"  {status.Name,-20} {status.Source,-12} {status.State,-14} {tools,4} tools{error}");
        }
        return builder.ToString().TrimEnd();
    }

    public static string RenderStatus(McpServerStatus status)
        => string.Join(Environment.NewLine,
            $"Server:      {status.Name}",
            $"Source:      {status.Source}",
            $"State:       {status.State}",
            $"Tools:       {status.ToolCount}",
            $"ServerInfo:  {status.ServerInfo ?? "-"}",
            $"LastError:   {status.LastError ?? "-"}");

    public static string RenderConnectResult(bool success, string name, McpServerStatus status)
        => success
            ? status.State switch
            {
                "connected" => $"Connected MCP server '{name}' ({status.ToolCount} tools).",
                "error" => $"[mcp] error: {status.LastError ?? "connect failed"}: '{name}'",
                _ => $"Connecting MCP server '{name}' ({status.State})."
            }
            : $"[mcp] error: Unknown MCP server '{name}'.";

    public static string RenderDisconnectResult(bool success, string name)
        => success ? $"Disconnected MCP server '{name}'." : $"[mcp] error: Unknown MCP server '{name}'.";

    public static string RenderHelp()
        => string.Join(Environment.NewLine,
            "Usage: /mcp <subcommand> [server]",
            "  list                List configured MCP servers with state and tool counts.",
            "  status <server>     Show detailed status for one server.",
            "  connect <server>    Connect a configured server.",
            "  disconnect <server> Disconnect a server.",
            "  reload              Re-read settings and reconcile live servers.",
            "  login <server>      Run the OAuth login flow for a server.",
            "  logout <server>     Remove stored credentials for a server.");

    private async Task SendAsync(string text, bool isError, CancellationToken cancellationToken)
        => await _api.SendMessageAsync(AgentMessages.User(isError ? $"[mcp] error: {text}" : text), cancellationToken: cancellationToken);

    private Task SendErrorAsync(string text, CancellationToken cancellationToken)
        => SendAsync(text, true, cancellationToken);
}
