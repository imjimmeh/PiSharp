using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;
using PiSharp.Plugins.ProtocolJsonRpc.Process;

namespace PiSharp.Plugins.Debug;

public enum DebugSessionState
{
    Spawning,
    Initializing,
    Attaching,
    Attached,
    Running,
    Stopped,
    Disconnecting,
    Disposed,
    Failed,
}

/// <summary>
/// Daemon-side debug session registry: one adapter process per session, a state machine
/// driven by attach lifecycle + adapter events, and idle disposal. Sessions survive tool-call
/// boundaries. A session is removed from the registry when it is disconnected, receives
/// <c>exited</c>/<c>terminated</c>, fails to attach, or is idle-swept; a session whose
/// adapter process dies mid-debug transitions to <see cref="DebugSessionState.Failed"/> with
/// the last stderr line and remains visible via <see cref="List"/>.
/// </summary>
public sealed class DebugSessionRegistry : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, DebugAdapterConfig> _configs;
    private readonly string _cwd;
    private readonly TimeSpan _idleTimeout;
    private readonly IServerProcessFactory _processFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private int _nextSessionId;
    private int _disposed;

    public DebugSessionRegistry(
        IReadOnlyDictionary<string, DebugAdapterConfig> configs,
        string cwd,
        TimeSpan idleTimeout,
        IServerProcessFactory? processFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        _configs = configs;
        _cwd = cwd;
        _idleTimeout = idleTimeout;
        _processFactory = processFactory ?? new SystemServerProcessFactory();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<DebugSessionRegistry>() ?? NullLogger<DebugSessionRegistry>.Instance;
    }

    public event Action<DebugSessionInfo>? SessionUpdated;

    /// <summary>(sessionId, event name, body) for adapter events; the extension re-emits as <c>debug_session_event</c>.</summary>
    public event Action<string, string, JsonElement>? AdapterEventReceived;

    public IReadOnlyCollection<string> Languages => _configs.Keys.ToArray();

    /// <summary>
    /// Spawns the adapter, runs the <c>initialize</c> → <c>attach</c>/<c>launch</c> handshake,
    /// and returns the session info. The DAP command is the attach body's <c>request</c>
    /// property (<c>launch</c> or <c>attach</c>, defaulting to <c>attach</c>); the body is
    /// interpolated (<c>${cwd}</c>/<c>${path}</c>) and merged with
    /// <paramref name="input"/>.<c>Request</c> before being sent as the arguments. On failure
    /// the session is removed and a <c>Failed</c> snapshot is fired.
    /// </summary>
    public async Task<DebugSessionInfo> AttachAsync(string language, DebugToolInput input, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_configs.TryGetValue(language, out var config))
        {
            throw new KeyNotFoundException(
                $"No debug adapter configured for language '{language}'. Configured adapters: {string.Join(", ", _configs.Keys)}.");
        }

        var sessionId = $"debug-{Interlocked.Increment(ref _nextSessionId)}";
        var entry = new SessionEntry(sessionId, language, config);
        lock (_sessions) _sessions[sessionId] = entry;
        Update(entry, DebugSessionState.Spawning);

        try
        {
            var server = new ManagedRpcServer(
                sessionId,
                config.Command,
                _processFactory,
                config.Env,
                config.WorkingDirectory ?? _cwd,
                RpcFrameShape.Dap,
                _loggerFactory);
            entry.Client = new DapClient(server, config.TimeoutMs, _loggerFactory);
            server.EnqueueInboundHandler((message, _) => HandleInboundAsync(entry, message));
            server.StandardErrorReceived += line => { if (line is not null) entry.StderrLines.Enqueue(line); };
            entry.Client.EventReceived += (name, body) => OnAdapterEvent(entry, name, body);

            Update(entry, DebugSessionState.Initializing);
            _ = await entry.Client.InitializeAsync(ct).ConfigureAwait(false);

            var attachBody = BuildAttachBody(config, input);
            var attachCommand = ReadAttachCommand(config.Attach);
            Update(entry, DebugSessionState.Attaching);
            _ = await entry.Client.AttachAsync(attachCommand, attachBody, ct).ConfigureAwait(false);

            entry.DebuggeeLabel = input.Path ?? language;
            Update(entry, DebugSessionState.Attached);
            return ToInfo(entry);
        }
        catch (Exception exception)
        {
            entry.LastError = LastErrorFor(entry, exception);
            Update(entry, DebugSessionState.Failed, entry.LastError);
            var client = entry.Client;
            entry.Client = null;
            lock (_sessions) _sessions.Remove(sessionId);
            if (client is not null) await DisposeClientAsync(client).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns the session's client. Throws <see cref="KeyNotFoundException"/> for removed
    /// sessions; a session whose adapter process has exited transitions to
    /// <see cref="DebugSessionState.Failed"/> (last stderr line as the error) and throws.
    /// </summary>
    public Task<DapClient> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                throw new KeyNotFoundException($"Unknown debug session '{sessionId}'. Attach first, or list sessions with op=list.");
            }

            if (entry.State is DebugSessionState.Failed)
            {
                throw FailedSessionException(entry);
            }

            if (entry.State is DebugSessionState.Disconnecting or DebugSessionState.Disposed)
            {
                throw new InvalidOperationException($"Debug session '{sessionId}' is {entry.State.ToString().ToLowerInvariant()}.");
            }

            if (entry.Client?.Server.HasExited == true)
            {
                entry.LastError = LastErrorFor(entry, new IOException("adapter process exited"));
                entry.State = DebugSessionState.Failed;
                entry.LastActivity = DateTimeOffset.UtcNow;
                var client = entry.Client;
                entry.Client = null;
                SessionUpdated?.Invoke(ToInfo(entry));
                _ = DisposeClientAsync(client);
                throw FailedSessionException(entry);
            }

            entry.LastActivity = DateTimeOffset.UtcNow;
            return Task.FromResult(entry.Client!);
        }
    }

    /// <summary>Sessions in creation order; includes <see cref="DebugSessionState.Failed"/> sessions.</summary>
    public IReadOnlyList<DebugSessionInfo> List()
    {
        lock (_sessions)
        {
            return _sessions.Values.Select(ToInfo).ToArray();
        }
    }

    /// <summary>Explicit disconnect: <c>disconnect</c> request (best-effort), adapter dispose, removal.</summary>
    public async Task DisconnectAsync(string sessionId, CancellationToken ct = default)
    {
        SessionEntry? entry = null;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out entry)) return;
            if (entry.State is DebugSessionState.Disposed or DebugSessionState.Disconnecting or DebugSessionState.Failed) return;
            Update(entry, DebugSessionState.Disconnecting);
        }

        if (entry.Client is not null)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                _ = await entry.Client.DisconnectAsync(null, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException or JsonRpcRemoteException)
            {
                _logger.LogDebug(exception, "Disconnect request for session '{SessionId}' failed; disposing anyway.", sessionId);
            }
        }

        var client = entry.Client;
        entry.Client = null;
        await DisposeClientAsync(client).ConfigureAwait(false);
        lock (_sessions) _sessions.Remove(sessionId);
        Update(entry, DebugSessionState.Disposed);
    }

    /// <summary>Disposes sessions with no tool traffic for longer than the idle timeout and removes them (plus any disposed entries).</summary>
    public async Task DisposeIdleAsync(CancellationToken ct = default)
    {
        List<SessionEntry> toRemove = [];
        lock (_sessions)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _sessions.Values)
            {
                if (entry.State is DebugSessionState.Disposed || now - entry.LastActivity >= _idleTimeout)
                {
                    toRemove.Add(entry);
                }
            }
        }

        foreach (var entry in toRemove)
        {
            _logger.LogInformation("Disposing idle debug session '{SessionId}' (language {Language}).", entry.SessionId, entry.Language);
            var client = entry.Client;
            entry.Client = null;
            await DisposeClientAsync(client).ConfigureAwait(false);
            lock (_sessions) _sessions.Remove(entry.SessionId);
            Update(entry, DebugSessionState.Disposed);
        }
    }

    private async Task<object?> HandleInboundAsync(SessionEntry entry, InboundRpcMessage message)
    {
        if (message.Method is not null && message.Method.StartsWith("event:", StringComparison.Ordinal))
        {
            var eventName = message.Method["event:".Length..];
            var body = message.Params ?? default;
            entry.Client?.RouteEvent(eventName, body);
            AdapterEventReceived?.Invoke(entry.SessionId, eventName, body);
            return null;
        }

        return message.IsNotification ? null : new JsonRpcError(-32600, $"Unsupported adapter request: {message.Method}");
    }

    private void OnAdapterEvent(SessionEntry entry, string eventName, JsonElement body)
    {
        switch (eventName)
        {
            case "stopped":
                Update(entry, DebugSessionState.Stopped);
                break;
            case "continued":
                Update(entry, DebugSessionState.Running);
                break;
            case "exited":
            case "terminated":
                var client = entry.Client;
                entry.Client = null;
                lock (_sessions) _sessions.Remove(entry.SessionId);
                Update(entry, DebugSessionState.Disposed);
                _ = DisposeClientAsync(client);
                break;
        }

        AdapterEventReceived?.Invoke(entry.SessionId, eventName, body);
    }

    private async Task DisposeClientAsync(DapClient? client)
    {
        if (client is null) return;
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Dispose of adapter failed; process already gone.");
        }
    }

    private JsonElement BuildAttachBody(DebugAdapterConfig config, DebugToolInput input)
    {
        var resolvedPath = input.Path is null ? null : Path.GetFullPath(Path.IsPathRooted(input.Path) ? input.Path : Path.Combine(_cwd, input.Path));

        JsonElement body = config.Attach is { ValueKind: JsonValueKind.Object } attach
            ? DebugAdapterConfigParser.Interpolate(attach, _cwd, resolvedPath)
            : JsonSerializer.SerializeToElement(new JsonObject());

        if (input.Request is { ValueKind: JsonValueKind.Object } request)
        {
            var root = body.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(body.GetRawText()) as JsonObject ?? new JsonObject()
                : new JsonObject();
            foreach (var property in request.EnumerateObject())
            {
                root[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }

            body = JsonSerializer.SerializeToElement(root);
        }

        return body;
    }

    private static string ReadAttachCommand(JsonElement? attach)
        => attach is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty("request", out var request)
            && request.ValueKind == JsonValueKind.String
            && request.GetString() is { Length: > 0 } command
                ? command
                : "attach";

    private void Update(SessionEntry entry, DebugSessionState state, string? lastError = null)
    {
        entry.State = state;
        if (lastError is not null) entry.LastError = lastError;
        entry.LastActivity = DateTimeOffset.UtcNow;
        SessionUpdated?.Invoke(ToInfo(entry));
    }

    private static InvalidOperationException FailedSessionException(SessionEntry entry)
        => new($"Debug session '{entry.SessionId}' has exited{(entry.LastError is null ? "" : $": {entry.LastError}")}.");

    private string LastErrorFor(SessionEntry entry, Exception exception)
        => entry.StderrLines.TryPeek(out var lastLine) && !string.IsNullOrWhiteSpace(lastLine)
            ? lastLine
            : exception.Message;

    private DebugSessionInfo ToInfo(SessionEntry entry)
        => new(
            entry.SessionId,
            entry.Language,
            entry.State.ToString(),
            entry.CreatedAt,
            entry.DebuggeeLabel,
            entry.LastError);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        List<SessionEntry> entries;
        lock (_sessions)
        {
            entries = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var entry in entries)
        {
            var client = entry.Client;
            entry.Client = null;
            await DisposeClientAsync(client).ConfigureAwait(false);
        }
    }

    private sealed class SessionEntry(string sessionId, string language, DebugAdapterConfig config)
    {
        public string SessionId { get; } = sessionId;

        public string Language { get; } = language;

        public DebugAdapterConfig Config { get; } = config;

        public DapClient? Client { get; set; }

        public DebugSessionState State { get; set; } = DebugSessionState.Spawning;

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

        public string? DebuggeeLabel { get; set; }

        public string? LastError { get; set; }

        public Queue<string> StderrLines { get; } = new();
    }
}
