using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Coordination;

public static class CoordinationTools
{
    private const int MaxBodyLength = 8192;

    public sealed record SendArgs(string To, string Body);
    public sealed record InboxArgs(bool IncludeRead = false, int Limit = 20);
    public sealed record RosterArgs(bool IncludeInactive = false);
    public sealed record InboxResult(AgentToolResult<object?> Output, DateTimeOffset? NewCursor);

    private static string? ValidateSendOrNull(SendArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.To) || args.To.StartsWith("__invalid_type_"))
            return "'to' field must be a non-empty string value.";

        if (string.IsNullOrWhiteSpace(args.Body))
            return "'body' field is required and must not be empty or whitespace.";

        if (args.Body.Length > MaxBodyLength)
            return $"Message body exceeds maximum length of {MaxBodyLength} characters.";

        return null;
    }

    public static async Task<AgentToolResult<object?>> RosterAsync(
        DaemonConnection? connection,
        CancellationToken cancellationToken)
    {
        if (connection is null)
            return DiagnosticResult("Not connected to coordination daemon.");

        var roster = await connection.Client.GetRosterAsync(cancellationToken);

        if (roster.Agents.Length == 0)
            return TextResult("No agents in the coordination roster.");

        var sb = new StringBuilder();
        sb.AppendLine("# Coordination Roster");
        sb.AppendLine();

        foreach (var agent in roster.Agents)
        {
            sb.Append("- ");
            sb.Append("**");
            sb.Append(agent.AgentId);
            sb.Append("** ");
            sb.Append("(pid ");
            sb.Append(agent.ProcessId);
            sb.Append(", cwd: `");
            sb.Append(agent.Cwd);
            sb.Append("`, registered ");
            sb.Append(agent.RegisteredAt.ToString("O"));
            sb.AppendLine(")");
        }

        return TextResult(sb.ToString());
    }

    public static async Task<AgentToolResult<object?>> SendAsync(
        DaemonConnection? connection,
        string agentId,
        SendArgs args,
        CancellationToken cancellationToken)
    {
        if (connection is null)
            return DiagnosticResult("Not connected to coordination daemon.");

        var validationError = ValidateSendOrNull(args);
        if (validationError is not null)
            return TextResult(validationError);

        var messageId = Guid.NewGuid().ToString("N");
        await connection.Client.SendMessageAsync(messageId, agentId, args.To, args.Body, cancellationToken);

        return TextResult($"Message {messageId} sent to '{args.To}'.");
    }

    public static async Task<InboxResult> InboxAsync(
        DaemonConnection? connection,
        string agentId,
        InboxArgs args,
        DateTimeOffset? lastReadCursor,
        CancellationToken cancellationToken)
    {
        if (connection is null)
            return new InboxResult(DiagnosticResult("Not connected to coordination daemon."), null);

        DateTimeOffset? since = args.IncludeRead ? null : lastReadCursor;

        var inbox = await connection.Client.GetInboxAsync(agentId, sinceTimestamp: since, cancellationToken);

        var messages = inbox.Messages;
        if (args.Limit > 0 && messages.Length > args.Limit)
            messages = messages[^args.Limit..];

        if (messages.Length == 0)
            return new InboxResult(TextResult("No messages in inbox."), lastReadCursor);

        var sb = new StringBuilder();
        sb.AppendLine("# Inbox");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            sb.Append("- **[");
            sb.Append(msg.FromAgentId);
            sb.Append("]** (");
            sb.Append(msg.Timestamp.ToString("O"));
            sb.AppendLine(")");

            var bodyLines = msg.Body.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append("  > ");
                sb.AppendLine(line);
            }
        }

        DateTimeOffset? newCursor = args.IncludeRead ? null : messages[^1].Timestamp;

        return new InboxResult(TextResult(sb.ToString()), newCursor);
    }

    public static SendArgs DeserializeSendArgs(JsonElement parameters)
    {
        string to;
        if (parameters.TryGetProperty("to", out var toEl))
        {
            if (toEl.ValueKind != JsonValueKind.String)
                to = $"__invalid_type_{toEl.ValueKind}__";
            else
                to = toEl.GetString()!;
        }
        else
        {
            to = "all";
        }

        var body = parameters.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString()!
            : string.Empty;
        return new SendArgs(to, body);
    }

    public static InboxArgs DeserializeInboxArgs(JsonElement parameters)
    {
        var includeRead = parameters.TryGetProperty("includeRead", out var irEl) && irEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? irEl.GetBoolean()
            : false;
        var limit = parameters.TryGetProperty("limit", out var limEl) && limEl.ValueKind == JsonValueKind.Number
            ? limEl.GetInt32()
            : 20;
        return new InboxArgs(includeRead, limit);
    }

    private static AgentToolResult<object?> TextResult(string text)
    {
        return new AgentToolResult<object?>([new TextContent(text)], null!);
    }

    private static AgentToolResult<object?> DiagnosticResult(string message)
    {
        return new AgentToolResult<object?>([new TextContent(message)], null!);
    }
}
