using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

namespace PiSharp.AgentMessaging;

/// <summary>
/// Bridges routed agent messages and roster changes to the live session:
/// messages addressed to the local agent are injected into the harness via
/// <c>SendMessageAsync</c> with the mapped delivery semantics, and both
/// message and roster events are pushed to the daemon wire on the C3
/// custom-event lane so clients render them.
/// </summary>
public sealed class AgentMessageEventAdapter
{
    private readonly IExtensionApi _api;
    private readonly string _localAgentId;

    public AgentMessageEventAdapter(IExtensionApi api, string localAgentId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _localAgentId = localAgentId ?? throw new ArgumentNullException(nameof(localAgentId));
    }

    public string LocalAgentId => _localAgentId;

    /// <summary>
    /// Delivery callback wired into the router: injects into the local harness
    /// when the message targets this session, then publishes the
    /// <c>agent_message</c> event on the wire.
    /// </summary>
    public async Task DeliverAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        if (message.ToAgentIds.Contains(_localAgentId, StringComparer.Ordinal))
        {
            var (delivery, triggerTurn) = MapDelivery(message.Delivery);
            await _api.SendMessageAsync(
                AgentMessages.User($"[Agent Message from {message.FromAgentId}]: {message.Body}"),
                delivery,
                triggerTurn,
                cancellationToken).ConfigureAwait(false);
        }

        await EmitMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes an <c>agent_message</c> event on the daemon wire.</summary>
    public Task EmitMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => _api.EmitClientEventAsync(AgentMessagingEventNames.AgentMessage, message, cancellationToken);

    /// <summary>Publishes an <c>agent_roster_update</c> event on the daemon wire.</summary>
    public Task EmitRosterAsync(AgentRoster roster, CancellationToken cancellationToken = default)
        => _api.EmitClientEventAsync(AgentMessagingEventNames.AgentRosterUpdate, roster, cancellationToken);

    /// <summary>Maps the plugin delivery semantics onto the harness delivery surface.</summary>
    internal static (ExtensionMessageDelivery Delivery, bool TriggerTurn) MapDelivery(AgentMessageDelivery delivery)
        => delivery switch
        {
            AgentMessageDelivery.Steer => (ExtensionMessageDelivery.Steer, false),
            AgentMessageDelivery.FollowUp => (ExtensionMessageDelivery.FollowUp, false),
            _ => (ExtensionMessageDelivery.NextTurn, true),
        };
}
