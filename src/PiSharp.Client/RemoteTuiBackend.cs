using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Extensions;
using PiSharp.Runtime;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;
using PiSharp.Tui.Interactive;

namespace PiSharp.Client;

/// <summary>
/// Remote <see cref="ITuiRuntimeFacade"/> driving a daemon-hosted TUI session over
/// <see cref="ClientSessionConnection"/>. Incoming server envelopes are folded into
/// <see cref="ClientSessionState"/> via <see cref="ClientEventReducer.Apply"/>, converted back to
/// <see cref="AgentHarnessEvent"/>s by <see cref="ClientToTuiAdapter"/>, and replayed to subscribed
/// listeners — so TuiHarnessSubscription's existing reducer keeps rendering without a TUI-side state hook.
/// The transcript seed comes from the TUI's own snapshot hydration (<c>GetSessionSnapshotAsync</c>).
/// </summary>
public sealed class RemoteTuiBackend : ITuiRuntimeFacade, IAsyncDisposable
{
    /// <summary>
    /// Receives <see cref="ServerUiIntent"/>s bridged from the daemon (the <c>ui_request</c> event).
    /// When unset, every request is auto-cancelled with <see cref="ServerUiResponse.Cancelled"/>.
    /// Task3.4 wires <see cref="ExtensionUiBridgeHost.HandleAsync(ExtensionUiIntent, CancellationToken)"/>
    /// here — the two intent shapes are identical.
    /// </summary>
    public Func<ServerUiIntent, CancellationToken, Task<ServerUiResponse>>? UiRequestHandler { get; set; }

    /// <summary>
    /// Resolves the daemon session id when the backend wasn't constructed with one; captured from the
    /// fork response when the daemon switches live sessions.
    /// </summary>
    public string? ServerSessionId { get; set; }

    /// <summary>Raised after a completed gap-recovery resync.</summary>
    public event Action? Resynced;

    private readonly ClientSessionConnection _connection;
    private readonly ILogger? _logger;
    private readonly Channel<ServerEventEnvelope> _inbox = Channel.CreateUnbounded<ServerEventEnvelope>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly List<Func<AgentHarnessEvent, CancellationToken, Task>> _listeners = [];

    private ClientSessionState _state = ClientSessionState.Empty;
    private ModelDescriptor? _model;
    private ThinkingLevel _thinkingLevel = ThinkingLevel.Off;
    private bool _turnActive;
    private long _maxSequence;
    private bool _recovering;

    public RemoteTuiBackend(ClientSessionConnection connection, ILogger? logger = null)
    {
        _connection = connection;
        _logger = logger;
        connection.EventReceived += OnEnvelope;
        _ = Task.Run(() => ProcessInboxAsync(_cts.Token), CancellationToken.None);
    }

    // --- ITuiRuntimeFacade ---

    public AgentHarnessPhase Phase
    {
        get
        {
            lock (_sync)
            {
                if (_state.IsCompacting) return AgentHarnessPhase.Compaction;
                if (_state.IsBusy || _turnActive) return AgentHarnessPhase.Turn;
                return AgentHarnessPhase.Idle;
            }
        }
    }

    public ModelDescriptor Model
    {
        get
        {
            lock (_sync)
            {
                return _model ?? new ModelDescriptor(string.Empty, string.Empty, string.Empty);
            }
        }
    }

    public ThinkingLevel ThinkingLevel
    {
        get
        {
            lock (_sync)
            {
                return _thinkingLevel;
            }
        }
    }

    public IReadOnlyList<string> ActiveToolNames
    {
        get
        {
            lock (_sync)
            {
                return _state.Transcript
                    .Where(row => row.IsTool && row.IsPending && !string.IsNullOrWhiteSpace(row.ToolName))
                    .Select(row => row.ToolName!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IDisposable Subscribe(Func<AgentHarnessEvent, CancellationToken, Task> listener)
    {
        lock (_sync)
        {
            _listeners.Add(listener);
        }

        return new Subscription(() =>
        {
            lock (_sync)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>Sends the <c>abort</c> command; fire-and-forget like the local runtime's abort.</summary>
    public void Abort()
    {
        var sessionId = ServerSessionId;
        if (sessionId is null) return;
        _ = SendAsync(new ServerCommandEnvelope(ServerCommandTypes.Abort, ServerSessionId: sessionId), CancellationToken.None);
    }

    public async Task PromptAsync(string text, IReadOnlyList<ImageContent> images, CancellationToken token)
    {
        var sessionId = RequireSessionId();
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Prompt, ServerSessionId: sessionId),
            new { message = text, images },
            token);
        ThrowOnFailure(response, "prompt");
    }

    /// <summary>Steers the daemon with the message's text via the <c>steer</c> command; fire-and-forget.</summary>
    public void Steer(AgentMessage message)
    {
        var sessionId = ServerSessionId;
        if (sessionId is null) return;
        _ = SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Steer, ServerSessionId: sessionId),
            new { message = ExtractText(message), triggerIfIdle = false },
            CancellationToken.None);
    }

    public Func<CancellationToken, Task>? OnHarnessReplaced { get; set; }

    // --- connection lifecycle ---

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken token = default)
        => _connection.ConnectAsync(uri, apiKey, token);

    public async ValueTask DisposeAsync()
    {
        _connection.EventReceived -= OnEnvelope;
        _inbox.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _inbox.Reader.Completion;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
        }

        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Resynchronizes after a detected gap: re-fetch <c>get_state</c>, fold the snapshot (preserving
    /// the accumulated transcript), then re-attach from the pre-snapshot watermark so the retained
    /// replay redelivers the dropped events (no-op for an existing pump on this connection).
    /// </summary>
    public async Task RecoverFromGapAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_recovering) return;
            _recovering = true;
        }

        try
        {
            var sessionId = RequireSessionId();
            long sinceSequence;
            lock (_sync)
            {
                sinceSequence = _maxSequence;
            }

            var state = await GetStateAsync(token);
            lock (_sync)
            {
                _state = ClientToTuiAdapter.ToClientState(_state, state);
                _model = state.Model;
                _thinkingLevel = state.ThinkingLevel;
                _turnActive = state.IsBusy;
            }

            await SendAsync(
                new ServerCommandEnvelope(ServerCommandTypes.Attach, ServerSessionId: sessionId),
                new { sinceSequence },
                token);
            Resynced?.Invoke();
        }
        finally
        {
            lock (_sync)
            {
                _recovering = false;
            }
        }
    }



    // --- TuiHostOptions-shaped surface (Task3.4 wraps these into its delegates) ---

    public async Task<string?> GetSessionNameAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_state.SessionName is { } name) return name;
        }

        return (await GetStateAsync(token)).SessionName;
    }

    public async Task<TuiCommandDispatchResult> DispatchCommandAsync(TuiCommandDispatchRequest request, CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.RunCommand, ServerSessionId: RequireSessionId()),
            new { text = request.Text, options = (object?)null },
            token);
        if (!response.Success)
        {
            throw new InvalidOperationException($"run_command failed: {response.Error?.Code}: {response.Error?.Message}");
        }

        var result = FromServerPayload<ServerCommandResult>(response.Data);
        return result is null
            ? new TuiCommandDispatchResult(false)
            : new TuiCommandDispatchResult(result.Handled, result.ShouldExit);
    }

    public async Task<IReadOnlyList<string>> CompleteCommandAsync(string text, CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CompleteCommand, ServerSessionId: RequireSessionId()),
            new { text },
            token);
        return response.Success
            ? FromServerPayload<IReadOnlyList<string>>(response.Data) ?? []
            : [];
    }

    public async Task<TuiInputHookResult> ProcessInputAsync(string text, IReadOnlyList<ImageContent>? images, string source, CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.ProcessInput, ServerSessionId: RequireSessionId()),
            new { text, images, source },
            token);
        if (!response.Success)
        {
            throw new InvalidOperationException($"process_input failed: {response.Error?.Code}: {response.Error?.Message}");
        }

        var result = FromServerPayload<ProcessInputResult>(response.Data);
        return result is null
            ? new TuiInputHookResult(false, text, images)
            : new TuiInputHookResult(result.Handled, result.Text, result.Images);
    }

    public async Task<TuiThemeDocument?> GetThemeAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetTheme, ServerSessionId: RequireSessionId()),
            token);
        return response.Success ? FromServerPayload<TuiThemeDocument>(response.Data) : null;
    }

    public async Task<TuiSessionSnapshot> GetSessionSnapshotAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetSessionSnapshot, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success)
        {
            throw new InvalidOperationException($"get_session_snapshot failed: {response.Error?.Code}: {response.Error?.Message}");
        }

        var snapshot = FromServerPayload<ServerSessionSnapshot>(response.Data)
            ?? throw new InvalidOperationException("get_session_snapshot returned no snapshot.");
        return ClientToTuiAdapter.ToSessionSnapshot(snapshot);
    }

    public async Task ForkFromEntryAsync(string entryId, CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Fork, ServerSessionId: RequireSessionId()),
            new { entryId, newSessionId = (string?)null },
            token);
        ThrowOnFailure(response, "fork");

        // The daemon switched to a new live session: adopt it, then let the TUI refresh its snapshot.
        var state = FromServerPayload<ServerSessionState>(response.Data);
        if (state is not null)
        {
            lock (_sync)
            {
                _state = ClientToTuiAdapter.ToClientState(_state, state);
                _model = state.Model;
                _thinkingLevel = state.ThinkingLevel;
                _turnActive = state.IsBusy;
                _maxSequence = state.HighWatermark;
            }

            ServerSessionId = state.ServerSessionId;
        }

        var onReplaced = OnHarnessReplaced;
        if (onReplaced is not null) _ = onReplaced(CancellationToken.None);
    }

    public async Task<IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>>> GetExtensionShortcutsAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetExtensionShortcuts, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success) return [];

        var items = FromServerPayload<IReadOnlyList<ShortcutWire>>(response.Data) ?? [];
        var shortcuts = new List<OwnedExtensionRegistration<ExtensionShortcutRegistration>>(items.Count);
        foreach (var item in items)
        {
            if (item.Value is null || string.IsNullOrWhiteSpace(item.Id)) continue;
            // The handler delegate is not serializable; remote shortcut invocation is a no-op for now.
            shortcuts.Add(new OwnedExtensionRegistration<ExtensionShortcutRegistration>(
                item.Id,
                item.SourceId ?? item.Id,
                new ExtensionShortcutRegistration(item.Value.Keys, item.Value.Description, static (_, _) => Task.CompletedTask)));
        }

        return shortcuts;
    }

    public async Task<ExtensionRegistry?> GetExtensionRegistryAsync(CancellationToken token = default)
    {
        // The registry graph is not constructible client-side (delegate registrations); on any
        // trouble the TUI falls back to no registry.
        await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetExtensionRegistry, ServerSessionId: RequireSessionId()),
            token);
        return null;
    }

    public async Task<TuiExtensionLoadStatus> GetExtensionLoadStatusAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetExtensionLoadStatus, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success) return new TuiExtensionLoadStatus(0, 0, 0, 0, 0);

        var summary = FromServerPayload<ExtensionLoadSummary>(response.Data);
        if (summary is null) return new TuiExtensionLoadStatus(0, 0, 0, 0, 0);

        var failures = summary.FailedDiagnostics is { Count: > 0 }
            ? summary.FailedDiagnostics.Select(failure => new TuiExtensionLoadFailure(failure.Path, failure.Diagnostic)).ToArray()
            : null;
        return new TuiExtensionLoadStatus(summary.Total, summary.Active, summary.BlockingActive, summary.Ready, summary.Failed, failures);
    }

    public async Task<IReadOnlyList<string>> GetStartupMessagesAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetStartupMessages, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success) return [];

        return FromServerPayload<ServerStartupMessages>(response.Data)?.Messages ?? [];
    }

    public async Task PostStartupChecksAsync(Func<string, Task> emit, CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.PostStartupChecks, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success)
        {
            // Older or delegate-less daemons answer not_available; startup-check lines then arrive
            // as system_message events on the normal stream (or never) — not a client failure.
            _logger?.LogDebug("post_startup_checks not available: {Code}: {Message}", response.Error?.Code, response.Error?.Message);
            return;
        }
    }

    public async Task CycleThinkingLevelAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CycleThinkingLevel, ServerSessionId: RequireSessionId()),
            token);
        ThrowOnFailure(response, "cycle_thinking_level");
        var payload = FromServerPayload<ThinkingLevelWire>(response.Data);
        if (payload is { Level: { } level } && ClientToTuiAdapter.TryParseThinkingLevel(level) is { } parsed)
        {
            lock (_sync)
            {
                _thinkingLevel = parsed;
            }
        }
    }

    public async Task<IReadOnlyList<ModelDescriptor>> GetAvailableModelsAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetAvailableModels, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success) return [];

        return FromServerPayload<IReadOnlyList<ModelDescriptor>>(response.Data) ?? [];
    }

    public async Task<IReadOnlyList<string>> GetCommandsAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetCommands, ServerSessionId: RequireSessionId()),
            token);
        return response.Success
            ? FromServerPayload<IReadOnlyList<string>>(response.Data) ?? []
            : [];
    }

    public async Task<string> GetLastAssistantTextAsync(CancellationToken token = default)
    {
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetLastAssistantText, ServerSessionId: RequireSessionId()),
            token);
        if (!response.Success) return string.Empty;

        return FromServerPayload<TextWire>(response.Data)?.Text ?? string.Empty;
    }

    // --- event pipeline ---

    private void OnEnvelope(ServerEventEnvelope envelope)
        => _inbox.Writer.TryWrite(envelope);

    private async Task ProcessInboxAsync(CancellationToken token)
    {
        try
        {
            await foreach (var envelope in _inbox.Reader.ReadAllAsync(token))
            {
                try
                {
                    await ApplyEnvelopeAsync(envelope, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogError(ex, "Failed to apply daemon envelope {Sequence} ({Type})", envelope.Sequence, envelope.Event.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    private async Task ApplyEnvelopeAsync(ServerEventEnvelope envelope, CancellationToken token)
    {
        AgentHarnessEvent? harnessEvent;
        bool isUiRequest;
        bool gap;

        lock (_sync)
        {
            if (envelope.Sequence <= _maxSequence)
            {
                // Already applied (idempotent replay) — nothing to do.
                return;
            }

            if (_maxSequence > 0 && envelope.Sequence > _maxSequence + 1)
            {
                _logger?.LogWarning(
                    "Gap in daemon event stream: last applied {Last}, received {Received}; resyncing",
                    _maxSequence, envelope.Sequence);
                gap = true;
                harnessEvent = null;
                isUiRequest = false;
            }
            else
            {
                gap = false;
                _maxSequence = envelope.Sequence;
                _state = ClientEventReducer.Apply(_state, envelope);
                TrackLifecycle(envelope);
                isUiRequest = envelope.Event.Type == "ui_request";
                harnessEvent = isUiRequest ? null : ClientToTuiAdapter.ToHarnessEvent(envelope);
            }
        }

        if (gap)
        {
            // Drop the gapped envelope: attach replay redelivers it from the resync point.
            _ = Task.Run(() => RecoverFromGapAsync(token), CancellationToken.None);
            return;
        }

        if (isUiRequest)
        {
            await HandleUiRequestAsync(envelope, token);
            return;
        }

        if (harnessEvent is null) return; // server-defined type without a TUI inverse — already folded

        Func<AgentHarnessEvent, CancellationToken, Task>[] listeners;
        lock (_sync)
        {
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            await listener(harnessEvent, token);
        }
    }

    /// <summary>
    /// Tracks turn/agent lifecycle locally — the reducer only knows busy/idle, not turn boundaries —
    /// and captures model/thinking-level payloads for the facade getters.
    /// </summary>
    private void TrackLifecycle(ServerEventEnvelope envelope)
    {
        switch (envelope.Event.Type)
        {
            case "agent_start":
            case "turn_start":
                _turnActive = true;
                break;
            case "agent_end":
            case "turn_end":
                _turnActive = false;
                break;
            case "model_select":
                if (ClientToTuiAdapter.ExtractModel(envelope.Event.Data) is { } model) _model = model;
                break;
            case "thinking_level_changed":
            case "thinking_level_select":
                if (ClientToTuiAdapter.ExtractThinkingLevel(envelope.Event.Data) is { } level) _thinkingLevel = level;
                break;
        }
    }

    private async Task HandleUiRequestAsync(ServerEventEnvelope envelope, CancellationToken token)
    {
        var intent = ClientToTuiAdapter.FromPayload<ServerUiIntent>(envelope.Event.Data);
        if (intent is null) return;

        ServerUiResponse response;
        var handler = UiRequestHandler;
        if (handler is null)
        {
            _logger?.LogDebug("No UI request handler configured; auto-cancelling request {RequestId}", intent.RequestId);
            response = new ServerUiResponse(intent.RequestId, null, Cancelled: true);
        }
        else
        {
            try
            {
                response = await handler(intent, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "UI request handler failed for {RequestId}; cancelling", intent.RequestId);
                response = new ServerUiResponse(intent.RequestId, null, Cancelled: true);
            }
        }

        await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.UiResponse, ServerSessionId: envelope.ServerSessionId),
            new { requestId = response.RequestId, value = response.Value, cancelled = response.Cancelled },
            CancellationToken.None);
    }

    // --- command plumbing ---

    private Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, CancellationToken token)
        => _connection.SendAsync(envelope, token);

    private Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, object payload, CancellationToken token)
        => _connection.SendAsync(envelope, payload, token);

    private static T? FromServerPayload<T>(object? data)
        => data is null
            ? default
            : JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(data, ServerJsonSerializer.Options),
                ServerJsonSerializer.Options);

    private async Task<ServerSessionState> GetStateAsync(CancellationToken token)
    {
        var sessionId = RequireSessionId();
        var response = await SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetState, ServerSessionId: sessionId),
            token);
        ThrowOnFailure(response, "get_state");
        return FromServerPayload<ServerSessionState>(response.Data)
            ?? throw new InvalidOperationException("get_state returned no state.");
    }

    private string RequireSessionId()
        => ServerSessionId ?? throw new InvalidOperationException(
            "No server session id; the daemon session has not been created or attached yet.");

    private static void ThrowOnFailure(ServerResponse response, string command)
    {
        if (!response.Success)
        {
            throw new InvalidOperationException($"Command '{command}' failed: {response.Error?.Code}: {response.Error?.Message}");
        }
    }

    private static string ExtractText(AgentMessage message)
    {
        var builder = new System.Text.StringBuilder();
        IReadOnlyList<MessageContent>? content = message switch
        {
            UserMessage user => user.Content,
            AssistantMessage assistant => assistant.Content,
            ToolResultMessage tool => tool.Content,
            _ => null,
        };

        if (content is not null)
        {
            foreach (var item in content)
            {
                if (item is TextContent text) builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    // --- wire payload shapes for response Data ---

    private sealed record ShortcutWire(string Id, string? SourceId, ShortcutValueWire? Value);
    private sealed record ShortcutValueWire(string Keys, string Description);
    private sealed record ThinkingLevelWire(string? Level);
    private sealed record TextWire(string? Text);
}
