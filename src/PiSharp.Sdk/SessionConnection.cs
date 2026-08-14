using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Client;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;

namespace PiSharp.Sdk;

/// <summary>
/// A live, attached view of one daemon session: folds the flat <see cref="AgentSessionEvent"/>
/// stream into a read-only <see cref="ClientSessionStateView"/> via the P01 reducer, exposes the raw
/// stream (<see cref="WithEvents"/>) and a batched change notification (<see cref="Changed"/>), and
/// maps typed command methods onto the daemon protocol. One <see cref="SessionConnection"/> owns one
/// WebSocket connection; disposing it detaches without stopping the daemon.
/// </summary>
public sealed class SessionConnection : IAsyncDisposable
{
    private const long NoSequence = 0;

    private readonly ClientSessionConnection _connection;
    private readonly string _serverSessionId;
    private readonly bool _autoDeclineUiRequests;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly Channel<ServerEventEnvelope> _inbox = Channel.CreateUnbounded<ServerEventEnvelope>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _sync = new();
    private readonly List<AgentSessionEvent> _replayBuffer = [];
    private readonly List<Channel<AgentSessionEvent>> _subscribers = [];
    private readonly Dictionary<string, AgentSessionEvent> _pendingToolCalls = new(StringComparer.Ordinal);
    private readonly Channel<UiRequest> _queuedUiRequests = Channel.CreateUnbounded<UiRequest>();

    private PiSharp.Client.ClientSessionState _state = PiSharp.Client.ClientSessionState.Empty;
    private Func<UiRequest, CancellationToken, Task<UiResponse>>? _uiRequestHandler;
    private long _maxSequence;
    private long _headSequence;
    private bool _attached;
    private bool _recovering;
    private int _disposed;

    private readonly Task _attachTask;
    private SessionConnection(
        ClientSessionConnection connection,
        string serverSessionId,
        AttachOptions options)
    {
        _connection = connection;
        _serverSessionId = serverSessionId;
        _autoDeclineUiRequests = options.AutoHandleUiRequests;
        connection.EventReceived += OnEnvelope;
        _processingTask = Task.Run(() => ProcessInboxAsync(_cts.Token), CancellationToken.None);
        _attachTask = AttachCoreAsync(options);
    }

    /// <summary>
    /// Creates the connection, sends <c>attach</c>, and awaits the attach response (the retained
    /// replay then streams on the same connection). Throws when attach fails.
    /// </summary>
    internal static async Task<SessionConnection> CreateAsync(
        ClientSessionConnection connection,
        string serverSessionId,
        AttachOptions options,
        CancellationToken ct)
    {
        var session = new SessionConnection(connection, serverSessionId, options);
        await session._attachTask.WaitAsync(ct);
        return session;
    }
    /// <summary>
    /// Read-only snapshot of the daemon session state, rebuilt on demand from the reducer.
    /// </summary>
    public ClientSessionStateView State
    {
        get
        {
            lock (_sync)
            {
                return new ClientSessionStateView(
                    _state,
                    _serverSessionId,
                    Math.Max(_headSequence, _maxSequence),
                    _attached,
                    _pendingToolCalls.Values.ToArray());
            }
        }
    }

    /// <summary>
    /// Raised after each applied batch of envelopes (one per frame on the wire) with the applied
    /// events and the sequence watermarks. Subscribers must not block; exceptions are swallowed.
    /// </summary>
    public event EventHandler<ClientSessionChangedEventArgs>? Changed;
    public IAsyncEnumerable<AgentSessionEvent> WithEvents(CancellationToken ct = default)
    {
        var subscription = SubscribeEvents();
        return EnumerateEventsAsync(subscription.Replay, subscription.Channel, ct);
    }

    // --- command methods (P01 §7.4) ---

    /// <summary>Sends a user prompt and runs a turn on the daemon (fire-and-forget on the server).</summary>
    public Task PromptAsync(string text, IReadOnlyList<ImageContent>? images = null, CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Prompt, ServerSessionId: _serverSessionId),
            new { message = text, images },
            "prompt",
            ct);

    /// <summary>Steers the running harness with an interruption message.</summary>
    public Task SteerAsync(string text, CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Steer, ServerSessionId: _serverSessionId),
            new { message = text, triggerIfIdle = false },
            "steer",
            ct);

    /// <summary>Queues a follow-up message for the next turn.</summary>
    public Task FollowUpAsync(string text, CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.FollowUp, ServerSessionId: _serverSessionId),
            new { message = text, triggerIfIdle = false },
            "follow_up",
            ct);

    /// <summary>Queues an empty next-turn message (the daemon drives the next turn when it is ready).</summary>
    public Task QueueNextTurnAsync(CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.QueueNextTurn, ServerSessionId: _serverSessionId),
            new { message = string.Empty, triggerIfIdle = false },
            "queue_next_turn",
            ct);

    /// <summary>Requests cancellation of the active or scheduled harness run.</summary>
    public Task AbortAsync(CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Abort, ServerSessionId: _serverSessionId),
            "abort",
            ct);

    /// <summary>Runs a context compaction with optional custom instructions.</summary>
    public Task CompactAsync(string? customInstructions = null, CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Compact, ServerSessionId: _serverSessionId),
            new { customInstructions },
            "compact",
            ct);

    /// <summary>
    /// Executes a daemon-side slash command (e.g. <c>"/help"</c>). Returns the daemon's
    /// <see cref="ServerCommandResult"/>; null when the response carried no result.
    /// </summary>
    public async Task<ServerCommandResult?> RunCommandAsync(string name, object? args = null, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.RunCommand, ServerSessionId: _serverSessionId),
            new { text = name, options = (object?)null },
            ct);
        ThrowOnFailure(response, "run_command");
        return FromServerPayload<ServerCommandResult>(response.Data);
    }

    /// <summary>Selects the model for this session; returns the resolved <see cref="ModelDescriptor"/>.</summary>
    public async Task<ModelDescriptor?> SetModelAsync(string provider, string modelId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.SetModel, ServerSessionId: _serverSessionId),
            new { provider, modelId },
            ct);
        ThrowOnFailure(response, "set_model");
        return FromServerPayload<ModelDescriptor>(response.Data);
    }

    /// <summary>Sets the thinking level for this session.</summary>
    public Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.SetThinkingLevel, ServerSessionId: _serverSessionId),
            new { level },
            "set_thinking_level",
            ct);

    /// <summary>Fetches the session snapshot (id, file, name, forkable branch entries).</summary>
    public async Task<ServerSessionSnapshot?> GetSessionSnapshotAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetSessionSnapshot, ServerSessionId: _serverSessionId),
            ct);
        ThrowOnFailure(response, "get_session_snapshot");
        return FromServerPayload<ServerSessionSnapshot>(response.Data);
    }

    /// <summary>
    /// Forks the session from an optional branch entry. The daemon switches to a new live session;
    /// returns its <see cref="ServerSessionState"/>, or null when the fork was cancelled.
    /// </summary>
    public async Task<ServerSessionState?> ForkAsync(string? entryId = null, string? newSessionId = null, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Fork, ServerSessionId: _serverSessionId),
            new { entryId, newSessionId },
            ct);
        ThrowOnFailure(response, "fork");
        return FromServerPayload<ServerSessionState>(response.Data);
    }

    /// <summary>Starts a fresh session in place; returns its state, or null when cancelled.</summary>
    public async Task<ServerSessionState?> NewSessionAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.NewSession, ServerSessionId: _serverSessionId),
            ct);
        ThrowOnFailure(response, "new_session");
        return FromServerPayload<ServerSessionState>(response.Data);
    }

    /// <summary>Switches to another persisted session; returns its state, or null when cancelled.</summary>
    public async Task<ServerSessionState?> SwitchSessionAsync(string sessionIdOrPath, CancellationToken ct = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.SwitchSession, ServerSessionId: _serverSessionId),
            new { sessionIdOrPath },
            ct);
        ThrowOnFailure(response, "switch_session");
        return FromServerPayload<ServerSessionState>(response.Data);
    }

    /// <summary>Sets the session display name.</summary>
    public Task SetSessionNameAsync(string name, CancellationToken ct = default)
        => SendCheckedAsync(
            new ServerCommandEnvelope(ServerCommandTypes.SetSessionName, ServerSessionId: _serverSessionId),
            new { name },
            "set_session_name",
            ct);

    /// <summary>
    /// Waits until the session reports idle (no running turn and no compaction), polling the
    /// reducer state. Returns immediately when the session is already idle.
    /// </summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var view = State;
            if (!view.IsBusy && !view.IsCompacting) return;
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    /// <summary>
    /// Installs (or removes) the headless responder for daemon <c>ui_request</c> events. Installing a
    /// handler drains any requests that queued while no handler was set. When the handler is null and
    /// <see cref="AttachOptions.AutoHandleUiRequests"/> is false, requests queue until a handler is
    /// installed (the daemon auto-cancels un-answered requests after ~5s); on dispose, queued
    /// requests are declined.
    /// </summary>
    public void SetUiRequestHandler(Func<UiRequest, CancellationToken, Task<UiResponse>>? handler)
    {
        lock (_sync)
        {
            _uiRequestHandler = handler;
        }

        if (handler is not null)
        {
            _ = Task.Run(async () =>
            {
                while (await _queuedUiRequests.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (_queuedUiRequests.Reader.TryRead(out var request))
                    {
                        await AnswerUiRequestAsync(request, handler);
                    }
                }
            }, _cts.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Decline any still-queued UI requests so a daemon-side wait is never left dangling.
        while (_queuedUiRequests.Reader.TryRead(out var queued))
        {
            await SendDeclineAsync(queued.RequestId);
        }
        _connection.EventReceived -= OnEnvelope;

        _cts.Cancel();
        _inbox.Writer.TryComplete();
        try
        {
            await _processingTask;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
        }

        Channel<AgentSessionEvent>[] subscribers;
        lock (_sync)
        {
            subscribers = _subscribers.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
        _cts.Dispose();
        await _connection.DisposeAsync();
    }

    // --- attach ---
    private async Task AttachCoreAsync(AttachOptions options)
    {
        var sinceSequence = options.SinceSequence ?? await ResolveDefaultSinceSequenceAsync(options);
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: _serverSessionId),
            new { sinceSequence },
            CancellationToken.None);
        ThrowOnFailure(response, "attach");

        var result = FromServerPayload<AttachResult>(response.Data);
        lock (_sync)
        {
            _headSequence = result?.HeadSequence ?? NoSequence;
            _attached = true;
        }

        // The retained replay follows on the same connection and is folded by the inbox loop.
    }

    /// <summary>
    /// Learns the retained-log head via <c>get_state</c> and computes the default replay start
    /// (head - ReplayWindow + 1, clamped to 1). The daemon skips the retained replay entirely when
    /// the requested sequence predates the oldest retained envelope, so attaching at 0 would lose
    /// all history; this mirrors the plan's "null → head - ReplayWindow" default.
    /// </summary>
    private async Task<long> ResolveDefaultSinceSequenceAsync(AttachOptions options)
    {
        var stateResponse = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetState, ServerSessionId: _serverSessionId),
            CancellationToken.None);
        if (stateResponse.Success && FromServerPayload<ServerSessionState>(stateResponse.Data) is { } snapshot)
        {
            return Math.Max(1, snapshot.HighWatermark - options.ReplayWindow + 1);
        }

        return NoSequence; // cannot learn the head — fall back to sequence 0 (live stream only)
    }
    private async Task RecoverFromGapAsync(CancellationToken ct)
    {
        bool alreadyRecovering;
        lock (_sync)
        {
            alreadyRecovering = _recovering;
            _recovering = true;
        }

        if (alreadyRecovering) return;

        try
        {
            long sinceSequence;
            lock (_sync)
            {
                sinceSequence = _maxSequence;
            }

            var stateResponse = await SendAsync(
                new ServerCommandEnvelope(ServerCommandTypes.GetState, ServerSessionId: _serverSessionId),
                ct);
            if (stateResponse.Success && FromServerPayload<ServerSessionState>(stateResponse.Data) is { } snapshot)
            {
                lock (_sync)
                {
                    _state = ClientToTuiAdapter.ToClientState(_state, snapshot);
                    _headSequence = Math.Max(_headSequence, snapshot.HighWatermark);
                }
            }

            // Re-attach from the pre-snapshot watermark so the retained replay redelivers the
            // dropped events (no-op for an existing pump on this connection).
            await SendAsync(
                new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: _serverSessionId),
                new { sinceSequence },
                ct);
        }
        finally
        {
            lock (_sync)
            {
                _recovering = false;
            }
        }
    }

    // --- event pipeline ---

    private void OnEnvelope(ServerEventEnvelope envelope)
    {
        _inbox.Writer.TryWrite(envelope);
    }

    private async Task ProcessInboxAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in _inbox.Reader.ReadAllAsync(ct))
            {
                await ApplyEnvelopeAsync(envelope, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    private async Task ApplyEnvelopeAsync(ServerEventEnvelope envelope, CancellationToken ct)
    {
        AgentSessionEvent? appliedEvent = null;
        bool gap;
        bool isUiRequest;

        lock (_sync)
        {
            if (envelope.Sequence <= _maxSequence)
            {
                // Idempotent replay — already applied.
                return;
            }

            if (_maxSequence > NoSequence && envelope.Sequence > _maxSequence + 1)
            {
                gap = true;
                isUiRequest = false;
            }
            else
            {
                gap = false;
                _maxSequence = envelope.Sequence;
                _state = ClientEventReducer.Apply(_state, envelope);
                appliedEvent = envelope.Event;
                isUiRequest = envelope.Event.Type == "ui_request";
                TrackToolLifecycle(envelope.Event);
                if (appliedEvent is not null)
                {
                    _replayBuffer.Add(appliedEvent);
                    foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(appliedEvent);
                }
            }
        }

        if (gap)
        {
            _ = Task.Run(() => RecoverFromGapAsync(ct), CancellationToken.None);
            return;
        }

        if (isUiRequest)
        {
            await HandleUiRequestAsync(envelope, ct);
        }

        if (appliedEvent is not null)
        {
            var args = new ClientSessionChangedEventArgs(
                [appliedEvent],
                envelope.Sequence,
                Math.Max(_headSequence, _maxSequence));
            try
            {
                Changed?.Invoke(this, args);
            }
            catch (Exception)
            {
                // A misbehaving subscriber must not stall the event stream.
            }
        }
    }

    private void TrackToolLifecycle(AgentSessionEvent flatEvent)
    {
        switch (flatEvent.Type)
        {
            case "tool_execution_start":
                if (TryGetToolCallId(flatEvent, out var started) && started is not null)
                    _pendingToolCalls[started] = flatEvent;
                break;
            case "tool_execution_end":
                if (TryGetToolCallId(flatEvent, out var ended) && ended is not null)
                    _pendingToolCalls.Remove(ended);
                break;
        }
    }

    private async Task HandleUiRequestAsync(ServerEventEnvelope envelope, CancellationToken ct)
    {
        var intent = FromServerPayload<ServerUiIntent>(envelope.Event.Data);
        if (intent is null) return;

        var request = new UiRequest(intent.RequestId, intent.Kind, intent.Title, intent.Message, intent.Options, intent.Component, intent.ExtensionId);

        Func<UiRequest, CancellationToken, Task<UiResponse>>? handler;
        lock (_sync)
        {
            handler = _uiRequestHandler;
        }

        if (_autoDeclineUiRequests || handler is null)
        {
            if (handler is null && !_autoDeclineUiRequests)
            {
                // No responder yet: queue the request until a handler is installed.
                _queuedUiRequests.Writer.TryWrite(request);
                return;
            }

            await SendDeclineAsync(request.RequestId);
            return;
        }

        await AnswerUiRequestAsync(request, handler);
    }

    private async Task AnswerUiRequestAsync(UiRequest request, Func<UiRequest, CancellationToken, Task<UiResponse>> handler)
    {
        UiResponse response;
        try
        {
            response = await handler(request, _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = new UiResponse(request.RequestId, null, Cancelled: true);
        }

        if (response.RequestId != request.RequestId)
        {
            response = new UiResponse(request.RequestId, response.Value, response.Cancelled);
        }

        await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.UiResponse, ServerSessionId: _serverSessionId),
            new { requestId = response.RequestId, value = response.Value, cancelled = response.Cancelled },
            CancellationToken.None);
    }

    private Task SendDeclineAsync(string requestId)
        => SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.UiResponse, ServerSessionId: _serverSessionId),
            new { requestId, value = (object?)null, cancelled = true },
            CancellationToken.None);

    // --- subscriptions ---

    private (IReadOnlyList<AgentSessionEvent> Replay, Channel<AgentSessionEvent> Channel) SubscribeEvents()
    {
        lock (_sync)
        {
            var channel = Channel.CreateUnbounded<AgentSessionEvent>(
                new UnboundedChannelOptions { SingleReader = true });
            _subscribers.Add(channel);
            return (_replayBuffer.ToArray(), channel);
        }
    }

    private async IAsyncEnumerable<AgentSessionEvent> EnumerateEventsAsync(
        IReadOnlyList<AgentSessionEvent> replay,
        Channel<AgentSessionEvent> channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var evt in replay)
        {
            ct.ThrowIfCancellationRequested();
            yield return evt;
        }

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }
    }

    // --- command plumbing ---

    private Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, CancellationToken ct)
        => _connection.SendAsync(envelope, ct);

    private Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, object payload, CancellationToken ct)
        => _connection.SendAsync(envelope, payload, ct);

    private async Task SendCheckedAsync(ServerCommandEnvelope envelope, object payload, string command, CancellationToken ct)
    {
        var response = await SendAsync(envelope, payload, ct);
        ThrowOnFailure(response, command);
    }

    private async Task SendCheckedAsync(ServerCommandEnvelope envelope, string command, CancellationToken ct)
    {
        var response = await SendAsync(envelope, ct);
        ThrowOnFailure(response, command);
    }

    private static void ThrowOnFailure(ServerResponse response, string command)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException($"Command '{command}' failed: {response.Error?.Code}: {response.Error?.Message}");
        }
    }


    private static T? FromServerPayload<T>(object? data)
    {
        if (data is null) return default;
        return JsonSerializer.Deserialize<T>(
            JsonSerializer.Serialize(data, ServerJsonSerializer.Options),
            ServerJsonSerializer.Options);
    }

    private static bool TryGetToolCallId(AgentSessionEvent flatEvent, out string? toolCallId)
    {
        if (FromServerPayload<ToolCallIdPayload>(flatEvent.Data) is { ToolCallId: { } id } payload)
        {
            toolCallId = id;
            return true;
        }

        toolCallId = null;
        return false;
    }

    private sealed record ToolCallIdPayload(string? ToolCallId);
}
