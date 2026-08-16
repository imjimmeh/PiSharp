using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;

namespace PiSharp.Client;

/// <summary>
/// Real <see cref="IClientTransport"/> over a <see cref="ClientWebSocket"/>: one JSON object per text
/// frame (either a <see cref="ServerResponse"/> or a <see cref="ServerEventEnvelope"/>), serialized
/// with <see cref="ServerJsonSerializer.Options"/> and distinguished by the <c>type</c> field.
/// Responses are correlated to in-flight commands by envelope id; events are pushed to the
/// <see cref="Events"/> channel in arrival order.
/// </summary>
public sealed class ClientWebSocketTransport : IClientTransport
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Per-command default timeouts: slow lanes (slash commands, input processing, session
    /// creation) get long polling windows instead of the blanket default. An explicit
    /// <c>timeoutOverride</c> on a send always takes precedence over this table.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> CommandTimeouts = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [ServerCommandTypes.RunCommand] = TimeSpan.FromMinutes(10),
        [ServerCommandTypes.ProcessInput] = TimeSpan.FromMinutes(10),
        [ServerCommandTypes.CreateSession] = TimeSpan.FromSeconds(90),
        [ServerCommandTypes.CompleteCommand] = TimeSpan.FromSeconds(15),
        [ServerCommandTypes.GetAvailableModels] = TimeSpan.FromSeconds(15),
        [ServerCommandTypes.GetTheme] = TimeSpan.FromSeconds(15),
        [ServerCommandTypes.ListSessions] = TimeSpan.FromSeconds(15),
        [ServerCommandTypes.Attach] = TimeSpan.FromSeconds(15),
    };

    // AgentSessionEvent is deliberately write-only on the wire: its JSON converter throws on Read.
    // Wire envelopes are therefore rebuilt from the raw frame with the payload preserved as a
    // JsonElement — exactly the shape ClientEventReducer documents for envelopes "arrived over the wire".
    private static readonly Func<string, object?, AgentSessionEvent> CreateSessionEvent = BuildEventFactory();

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerResponse>> _pending = new();
    private readonly Channel<ServerEventEnvelope> _events = Channel.CreateUnbounded<ServerEventEnvelope>();
    private readonly Channel<ServerResponse> _late = Channel.CreateBounded<ServerResponse>(64);
    private readonly CancellationTokenSource _readerCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeSpan _commandTimeout;
    private readonly ILogger _logger;
    private int _disposed;

    public ClientWebSocketTransport(ILogger logger, TimeSpan? commandTimeout = null)
    {
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _logger = logger;
    }

    public ChannelReader<ServerEventEnvelope> Events => _events.Reader;

    /// <summary>Responses that arrived after their command timed out (see <see cref="IClientTransport.LateResponses"/>).</summary>
    public ChannelReader<ServerResponse> LateResponses => _late.Reader;

    public async Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        // The daemon serves the socket at /ws; accept a bare ws://host:port uri as shorthand.
        var endpoint = uri.AbsolutePath is "/" or ""
            ? new UriBuilder(uri) { Path = "/ws" }.Uri
            : uri;

        _logger.LogInformation("Connecting to daemon at {Endpoint}", endpoint);
        try
        {
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            await _socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            _logger.LogError(exception, "WebSocket connection to {Endpoint} failed", endpoint);
            throw;
        }

        _logger.LogInformation("Connected to daemon at {Endpoint}", endpoint);
        _ = Task.Run(() => ReadLoopAsync(_readerCts.Token), CancellationToken.None);
    }

    public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct, TimeSpan? timeoutOverride = null)
        => SendCommandCoreAsync(envelope, payload: null, ct, timeoutOverride);

    public Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct, TimeSpan? timeoutOverride = null)
        => SendCommandCoreAsync(envelope, payload, ct, timeoutOverride);

    private async Task<ServerResponse> SendCommandCoreAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct, TimeSpan? timeoutOverride = null)
    {
        if (envelope.Id is null)
            throw new ArgumentException("Envelope Id is required for command correlation.", nameof(envelope));

        var json = BuildFrame(envelope, payload);
        var tcs = new TaskCompletionSource<ServerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(envelope.Id, tcs))
            throw new InvalidOperationException($"Command id '{envelope.Id}' is already in flight.");

        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogDebug("WebSocket command sent: {Command}", envelope.Type);
                await _socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            var effectiveTimeout = timeoutOverride
                ?? (CommandTimeouts.TryGetValue(envelope.Type, out var tableTimeout) ? tableTimeout : _commandTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(effectiveTimeout);
            try
            {
                return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Command {Command} ({Id}) timed out after {Timeout:0.#}s",
                    envelope.Type, envelope.Id, effectiveTimeout.TotalSeconds);
                return ServerResponse.Fail(envelope.Id, envelope.Type, "timeout",
                    $"No response for command '{envelope.Type}' within {effectiveTimeout.TotalSeconds:0.#}s.");
            }
        }
        finally
        {
            _pending.TryRemove(envelope.Id, out _);
        }
    }

    /// <summary>
    /// Builds the flat wire frame: envelope routing fields plus payload properties in one object,
    /// mirroring the server's flat request records (e.g. <c>attach</c>'s <c>sinceSequence</c>).
    /// </summary>
    private static byte[] BuildFrame(ServerCommandEnvelope envelope, object? payload)
    {
        var frame = payload is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(payload, ServerJsonSerializer.Options) as JsonObject
              ?? throw new InvalidOperationException("Command payload must serialize to a JSON object.");

        // Envelope routing fields take precedence over any payload fields with the same names.
        frame["type"] = envelope.Type;
        if (envelope.Id is not null) frame["id"] = envelope.Id;
        if (envelope.ServerSessionId is not null) frame["serverSessionId"] = envelope.ServerSessionId;
        return JsonSerializer.SerializeToUtf8Bytes(frame, ServerJsonSerializer.Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _readerCts.Cancel();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client disposed", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // socket already faulted or closed — nothing more to do
                }
            }
        }
        finally
        {
            _socket.Dispose();
            _readerCts.Dispose();
            _sendLock.Dispose();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(buffer, ct).ConfigureAwait(false);
                if (json is null) break; // server sent a Close frame

                try
                {
                    DispatchFrame(json);
                }
                catch (JsonException ex)
                {
                    // Malformed frame — drop it; a missed frame is recoverable via event replay.
                    _logger.LogWarning(ex, "Dropped malformed WebSocket frame: {Message}", ex.Message);
                }
            }

            _logger.LogInformation("WebSocket receive loop exited cleanly");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // transport disposed
        }
        catch (WebSocketException)
        {
            // connection dropped — nothing left to read
        }
        catch (ObjectDisposedException)
        {
            // transport disposed — nothing left to read
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WebSocket receive loop terminated unexpectedly");
        }
        finally
        {
            if (_pending.Count > 0)
            {
                _logger.LogWarning(
                    "WebSocket closed with {PendingCount} command(s) still awaiting a response",
                    _pending.Count);
            }

            _events.Writer.TryComplete();
        }
    }

    private async Task<string?> ReceiveTextAsync(byte[] buffer, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync((Memory<byte>)buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private void DispatchFrame(string json)
    {
        using var document = JsonDocument.Parse(json);
        var type = document.RootElement.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;

        switch (type)
        {
            case "response":
                var response = document.RootElement.Deserialize<ServerResponse>(ServerJsonSerializer.Options);
                if (response is not null) ResolveResponse(response);
                break;

            case "event":
                var envelope = ParseEventEnvelope(document.RootElement);
                _events.Writer.TryWrite(envelope);
                break;

            default:
                // Unknown frame type — ignore (future protocol extension).
                break;
        }
    }

    private void ResolveResponse(ServerResponse response)
    {
        if (response.Id is null) return;
        if (_pending.TryRemove(response.Id, out var tcs))
        {
            _logger.LogDebug("WebSocket response received: {Command}", response.Command);
            tcs.TrySetResult(response);
            return;
        }

        // Late answer for a timed-out command: surface instead of discarding (ShouldExit, session
        // creation results, etc. must not be lost). The client has no per-session destroy command,
        // so a timed-out create_session still relies on the server's 5-minute idle sweep — the 90s
        // window plus that sweep bound the orphan window.
        _logger.LogDebug("Late response arrived for timed-out command {Command} ({Id}); surfacing on the late lane", response.Command, response.Id);
        _late.Writer.TryWrite(response);
    }

    private static ServerEventEnvelope ParseEventEnvelope(JsonElement root)
    {
        var eventElement = root.GetProperty("event");
        var type = eventElement.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? "unknown"
            : "unknown";
        var payload = StripTypeProperty(eventElement);
        return new ServerEventEnvelope(
            "event",
            root.GetProperty("serverSessionId").GetString() ?? string.Empty,
            root.GetProperty("sequence").GetInt64(),
            root.GetProperty("timestamp").GetDateTimeOffset(),
            CreateSessionEvent(type, payload));
    }

    /// <summary>
    /// The wire flattens the event payload into the event object next to the "type" discriminator
    /// (mirroring <c>AgentSessionEventJsonConverter.Write</c>). The reducer consumes
    /// <see cref="AgentSessionEvent.Data"/>, so the remaining properties are repackaged as a
    /// JsonObject and returned as a single JsonElement.
    /// </summary>
    private static JsonElement StripTypeProperty(JsonElement eventElement)
    {
        if (eventElement.ValueKind != JsonValueKind.Object) return default;
        var payload = new JsonObject();
        foreach (var property in eventElement.EnumerateObject())
        {
            if (property.NameEquals("type")) continue;
            payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        return payload.Count == 0 ? default : JsonSerializer.SerializeToElement(payload);
    }

    private static Func<string, object?, AgentSessionEvent> BuildEventFactory()
    {
        var ctor = typeof(AgentSessionEvent).GetConstructor(
                       BindingFlags.Instance | BindingFlags.NonPublic,
                       binder: null,
                       [typeof(string), typeof(object)],
                       modifiers: null)
                   ?? throw new InvalidOperationException("AgentSessionEvent private constructor not found.");
        var type = Expression.Parameter(typeof(string));
        var data = Expression.Parameter(typeof(object));
        return Expression.Lambda<Func<string, object?, AgentSessionEvent>>(
            Expression.New(ctor, type, data), type, data).Compile();
    }
}
