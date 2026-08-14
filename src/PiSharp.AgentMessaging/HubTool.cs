using System.Text;
using System.Text.Json;
using PiSharp.Agent.Core.Tools;
using PiSharp.Abstractions.Messages;
using PiSharp.Tools;

namespace PiSharp.AgentMessaging;

/// <summary>
/// The model-facing <c>hub</c> tool: List (roster table), Send (validated
/// routing), Watch (target status snapshot + subscription notice), Steer
/// (steer a target). Backed by the in-process roster + router.
/// </summary>
public sealed class HubTool
{
    public const string ToolName = "hub";

    private readonly string _senderAgentId;
    private readonly AgentRosterService _roster;
    private readonly AgentMessageRouter _router;

    public HubTool(string senderAgentId, AgentRosterService roster, AgentMessageRouter router)
    {
        _senderAgentId = senderAgentId ?? throw new ArgumentNullException(nameof(senderAgentId));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public static JsonElement BuildSchema()
        => ToolSchemas.FromType<HubToolInput>();

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        var input = Deserialize(parameters);
        return input.Operation switch
        {
            HubOperation.List => List(input.Limit),
            HubOperation.Send => await SendAsync(input, cancellationToken).ConfigureAwait(false),
            HubOperation.Steer => await SteerAsync(input, cancellationToken).ConfigureAwait(false),
            HubOperation.Watch => Watch(input),
            _ => Ok("hub: unknown operation."),
        };
    }

    private AgentToolResult<object?> List(int? limit)
    {
        var family = _roster.GetFamily(_senderAgentId);
        if (limit is > 0)
            family = family.Take(limit.Value).ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("| id | name | role | status |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var agent in family)
        {
            builder.AppendLine($"| `{agent.AgentId}` | {EscapeCell(agent.Name)} | {EscapeCell(agent.Role)} | {agent.Status.ToString().ToLowerInvariant()} |");
        }

        return Ok(builder.ToString().TrimEnd());
    }

    private async Task<AgentToolResult<object?>> SendAsync(HubToolInput input, CancellationToken cancellationToken)
    {
        var targets = ResolveTargets(input.Target);
        var delivery = ParseDelivery(input.Delivery);
        var result = await _router.SendAsync(_senderAgentId, targets, input.Body ?? string.Empty, delivery, cancellationToken).ConfigureAwait(false);

        if (result.IsError)
            return Ok($"hub send failed: {result.ErrorMessage} ({result.ErrorCode})");

        var statuses = string.Join(", ", result.Recipients.Select(r => $"`{r.AgentId}`:{r.Status.ToString().ToLowerInvariant()}"));
        return Ok($"message {result.MessageId} sent to {statuses}.");
    }

    private async Task<AgentToolResult<object?>> SteerAsync(HubToolInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Target))
            return Ok("hub steer: target is required.");

        var result = await _router.SteerAsync(_senderAgentId, input.Target!, input.Body ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (result.IsError)
            return Ok($"hub steer failed: {result.ErrorMessage} ({result.ErrorCode})");

        return Ok($"steer {input.Target} accepted (message {result.MessageId}).");
    }

    private AgentToolResult<object?> Watch(HubToolInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Target))
            return Ok("hub watch: target is required.");

        if (!_roster.TryGet(input.Target!, out var agent))
            return Ok($"hub watch: unknown target '{input.Target}'.");

        var status = agent.Status.ToString().ToLowerInvariant();
        var mode = input.Watch == true ? "subscribed (live status will appear on the agent_message/agent_roster_update event lane)" : "one-shot snapshot";
        return Ok($"agent `{agent.AgentId}` status={status} role={agent.Role ?? "unknown"} mode={mode}.");
    }

    private IReadOnlyList<string> ResolveTargets(string? target)
        => string.IsNullOrWhiteSpace(target) ? [] : [target];

    private static AgentMessageDelivery ParseDelivery(string? delivery)
        => delivery?.ToLowerInvariant() switch
        {
            "follow_up" or "followup" => AgentMessageDelivery.FollowUp,
            "next_turn" or "nextturn" => AgentMessageDelivery.NextTurn,
            _ => AgentMessageDelivery.Steer,
        };

    internal static HubToolInput Deserialize(JsonElement parameters)
        => parameters.ValueKind == JsonValueKind.Object
            ? parameters.Deserialize<HubToolInput>(AgentMessagingJson.Options) ?? new HubToolInput(HubOperation.List)
            : new HubToolInput(HubOperation.List);

    private static string EscapeCell(string? value)
        => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);

    private static AgentToolResult<object?> Ok(string content)
        => new([new TextContent(content)], Details: null);
}
