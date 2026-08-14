using System.Collections.Concurrent;

namespace PiSharp.AgentMessaging;

/// <summary>
/// In-process agent message router: validates targets via
/// <see cref="MessageTargetValidator"/>, atomically enqueues to each
/// recipient, delivers live to Running recipients through the injected
/// delivery callback, queues for Passivated recipients in the persisted
/// outbox, and fails for Gone/unknown recipients. TTL expiry fails queued
/// messages; <see cref="ReplayAsync"/> re-attempts queued delivery on resume.
/// </summary>
public sealed class AgentMessageRouter : IAsyncDisposable
{
    private readonly AgentRosterService _roster;
    private readonly AgentMessageStore _store;
    private readonly AgentMessagingOptions _options;
    private readonly Func<AgentMessage, CancellationToken, Task>? _deliverAsync;

    // messageId → envelope for every routed message this process has seen.
    private readonly ConcurrentDictionary<string, AgentMessage> _messages = new(StringComparer.Ordinal);
    // (messageId, agentId) → per-recipient delivery outcome.
    private readonly ConcurrentDictionary<(string MessageId, string AgentId), AgentMessageStatus> _deliveries = new();
    // agentId → messages delivered to that agent (its inbox).
    private readonly ConcurrentDictionary<string, List<AgentMessage>> _inboxes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private bool _disposed;

    public AgentMessageRouter(
        AgentRosterService roster,
        AgentMessageStore store,
        AgentMessagingOptions options,
        Func<AgentMessage, CancellationToken, Task>? deliverAsync = null)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _deliverAsync = deliverAsync;
    }

    public AgentRosterService Roster => _roster;
    public AgentMessageStore Store => _store;

    /// <summary>
    /// Routes one message. Semantics: Running recipient → delivered live
    /// (<c>Delivered</c>); Passivated recipient → persisted and queued
    /// (<c>Queued</c>, replayed on resume); Gone/unknown/out-of-family/self
    /// target → rejected with <c>agent_message_target_invalid</c> before
    /// enqueue. <c>"all"</c>/<c>"parent"</c> resolve against the family.
    /// </summary>
    public async Task<AgentMessageSendResult> SendAsync(
        string fromAgentId,
        IReadOnlyList<string> rawTargets,
        string body,
        AgentMessageDelivery delivery = AgentMessageDelivery.Steer,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ErrorResult(AgentMessagingErrorCodes.Disabled, "agent messaging is disabled");

        if (body.Length > _options.MaxInboxMessageLength)
            return ErrorResult(AgentMessagingErrorCodes.BodyTooLong, $"message body exceeds the {_options.MaxInboxMessageLength} character limit");

        var validation = MessageTargetValidator.Validate(fromAgentId, _roster, rawTargets, _options.HubRoleWhitelist);
        if (!validation.Valid)
            return ErrorResult(validation.ErrorCode ?? AgentMessagingErrorCodes.TargetInvalid, validation.ErrorMessage ?? "target validation failed");

        var messageId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var message = new AgentMessage(messageId, fromAgentId, validation.Recipients, body, delivery, timestamp, AgentMessageStatus.Delivered);

        var recipients = new List<AgentMessageRecipient>(validation.Recipients.Count);
        var anyQueued = false;
        var anyDelivered = false;

        foreach (var recipientId in validation.Recipients)
        {
            var status = await RouteToRecipientAsync(message, recipientId, cancellationToken).ConfigureAwait(false);
            recipients.Add(new AgentMessageRecipient(recipientId, status));
            anyQueued |= status == AgentMessageStatus.Queued;
            anyDelivered |= status == AgentMessageStatus.Delivered;
        }

        message = message with { Status = anyQueued ? AgentMessageStatus.Queued : anyDelivered ? AgentMessageStatus.Delivered : AgentMessageStatus.Failed };
        _messages[messageId] = message;

        if (anyQueued)
            await _store.AppendAsync(message, cancellationToken).ConfigureAwait(false);

        return new AgentMessageSendResult(messageId, recipients);
    }

    /// <summary>Steers a single target (a <see cref="AgentMessageDelivery.Steer"/> send).</summary>
    public Task<AgentMessageSendResult> SteerAsync(
        string fromAgentId,
        string targetAgentId,
        string body,
        CancellationToken cancellationToken = default)
        => SendAsync(fromAgentId, [targetAgentId], body, AgentMessageDelivery.Steer, cancellationToken);

    /// <summary>
    /// The receiving agent's inbox: messages delivered to it, newest first.
    /// <paramref name="sinceMessageId"/> returns messages strictly newer than
    /// the given message id; <paramref name="limit"/> caps the count.
    /// </summary>
    public IReadOnlyList<AgentMessage> GetInbox(string agentId, string? sinceMessageId = null, int? limit = null)
    {
        if (!_inboxes.TryGetValue(agentId, out var inbox))
            return [];

        IEnumerable<AgentMessage> query = inbox;
        if (sinceMessageId is not null)
        {
            // Strictly-newer-than-the-marker window (unknown marker → full inbox).
            var cutoff = _messages.TryGetValue(sinceMessageId, out var marker)
                ? marker.Timestamp
                : DateTimeOffset.MinValue;
            query = query.Where(m => m.MessageId != sinceMessageId && m.Timestamp > cutoff);
        }

        IEnumerable<AgentMessage> ordered = query.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId);
        if (limit is > 0)
            ordered = ordered.Take(limit.Value);

        return ordered.ToArray();
    }

    /// <summary>Per-recipient delivery status of a previously routed message.</summary>
    public AgentMessageStatus GetDeliveryStatus(string messageId, string agentId)
        => _deliveries.TryGetValue((messageId, agentId), out var status) ? status : AgentMessageStatus.Failed;

    /// <summary>
    /// Re-attempts delivery of every persisted queued message to recipients
    /// that are Running now (startup replay / resume path). Messages whose
    /// recipients are all delivered are dropped from the outbox.
    /// </summary>
    public async Task ReplayAsync(CancellationToken cancellationToken = default)
    {
        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queued = (await _store.LoadAsync(cancellationToken).ConfigureAwait(false))
                .Where(m => m.Status == AgentMessageStatus.Queued)
                .ToArray();

            foreach (var message in queued)
            {
                var stillPending = new List<string>();
                foreach (var recipientId in message.ToAgentIds)
                {
                    if (_deliveries.TryGetValue((message.MessageId, recipientId), out var prior) && prior == AgentMessageStatus.Delivered)
                        continue;

                    var status = await RouteToRecipientAsync(message, recipientId, cancellationToken).ConfigureAwait(false);
                    if (status == AgentMessageStatus.Queued)
                        stillPending.Add(recipientId);
                }

                if (stillPending.Count == 0)
                    await _store.MarkFailedAsync(message.MessageId, cancellationToken).ConfigureAwait(false); // fully delivered → drop from outbox
            }
        }
        finally
        {
            _replayGate.Release();
        }
    }

    /// <summary>Fails queued messages that exceeded the configured TTL.</summary>
    public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
        => _store.CleanupExpiredAsync(_options.QueuedMessageTtlHours, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<AgentMessageStatus> RouteToRecipientAsync(AgentMessage message, string recipientId, CancellationToken cancellationToken)
    {
        if (!_roster.TryGet(recipientId, out var recipient))
        {
            _deliveries[(message.MessageId, recipientId)] = AgentMessageStatus.Failed;
            return AgentMessageStatus.Failed;
        }

        switch (recipient.Status)
        {
            case AgentStatus.Running:
                try
                {
                    if (_deliverAsync is not null)
                        await _deliverAsync(message, cancellationToken).ConfigureAwait(false);
                    DeliverToInbox(message, recipientId);
                    _deliveries[(message.MessageId, recipientId)] = AgentMessageStatus.Delivered;
                    return AgentMessageStatus.Delivered;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Delivery callback failed — queue instead of dropping.
                    _deliveries[(message.MessageId, recipientId)] = AgentMessageStatus.Queued;
                    return AgentMessageStatus.Queued;
                }

            case AgentStatus.Passivated:
                _deliveries[(message.MessageId, recipientId)] = AgentMessageStatus.Queued;
                return AgentMessageStatus.Queued;

            default: // Gone / unknown
                _deliveries[(message.MessageId, recipientId)] = AgentMessageStatus.Failed;
                return AgentMessageStatus.Failed;
        }
    }

    private void DeliverToInbox(AgentMessage message, string agentId)
    {
        var inbox = _inboxes.GetOrAdd(agentId, _ => []);
        lock (inbox)
        {
            inbox.Add(message);
        }
    }

    private static AgentMessageSendResult ErrorResult(string errorCode, string message)
        => new(string.Empty, [], errorCode, message);
}
