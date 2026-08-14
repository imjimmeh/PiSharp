using System.Text.Json;
using PiSharp.Agent.Core.Tools;
using PiSharp.Abstractions.Messages;
using PiSharp.Tools;

namespace PiSharp.AgentMessaging;

/// <summary>
/// The child-session messaging surface: <c>agent_message.send</c> (reply to
/// <c>"parent"</c>, a named sibling, or <c>"all"</c>) and
/// <c>agent_message.read</c> (pull this agent's inbox).
/// </summary>
public sealed class AgentMessageTool
{
    public const string ToolName = "agent_message";

    private readonly string _senderAgentId;
    private readonly AgentRosterService _roster;
    private readonly AgentMessageRouter _router;

    public AgentMessageTool(string senderAgentId, AgentRosterService roster, AgentMessageRouter router)
    {
        _senderAgentId = senderAgentId ?? throw new ArgumentNullException(nameof(senderAgentId));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public static JsonElement BuildSchema()
        => ToolSchemas.FromType<AgentMessageToolInput>();

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        var action = GetString(parameters, "action");
        return (action ?? string.Empty).ToLowerInvariant() switch
        {
            "send" => await SendAsync(parameters, cancellationToken).ConfigureAwait(false),
            "read" => Read(parameters),
            _ => Ok("agent_message: action must be 'send' or 'read'."),
        };
    }

    private async Task<AgentToolResult<object?>> SendAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var receiver = GetString(parameters, "receiver");
        var body = GetString(parameters, "body");
        if (string.IsNullOrWhiteSpace(receiver))
            return Ok("agent_message.send: receiver is required ('parent', an agent id, or 'all').");
        if (string.IsNullOrWhiteSpace(body))
            return Ok("agent_message.send: body is required.");

        var result = await _router.SendAsync(_senderAgentId, [receiver!], body!, AgentMessageDelivery.Steer, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
            return Ok($"agent_message.send failed: {result.ErrorMessage} ({result.ErrorCode})");

        var statuses = string.Join(", ", result.Recipients.Select(r => $"`{r.AgentId}`:{r.Status.ToString().ToLowerInvariant()}"));
        return Ok($"sent to {statuses} (message {result.MessageId}).");
    }

    private AgentToolResult<object?> Read(JsonElement parameters)
    {
        var since = GetString(parameters, "since");
        var limit = GetInt(parameters, "limit");
        var inbox = _router.GetInbox(_senderAgentId, since, limit);

        if (inbox.Count == 0)
            return Ok("agent_message.read: no messages.");

        var lines = inbox.Select(m =>
            $"- **{m.FromAgentId}** ({m.Delivery.ToString().ToLowerInvariant()}, {m.Timestamp:O}): {m.Body}");
        return Ok("agent_message.read:\n" + string.Join("\n", lines));
    }

    private static string? GetString(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static AgentToolResult<object?> Ok(string content)
        => new([new TextContent(content)], Details: null);
}

/// <summary>Input record of the <c>agent_message</c> tool.</summary>
public sealed record AgentMessageToolInput(string? Action = null, string? Receiver = null, string? Body = null, string? Since = null, int? Limit = null);
