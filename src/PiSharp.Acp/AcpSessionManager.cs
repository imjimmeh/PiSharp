using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Runtime;

namespace PiSharp.Acp;

/// <summary>An active ACP session identity, mapping to a PiSharp JSONL session (plan §3.4).</summary>
public sealed record AcpSessionInfo(string SessionId, string JsonlId, string Cwd);

/// <summary>
/// ACP session registry over a single <see cref="SessionRuntime"/> (plan §4.2/§7). v1 enforces one
/// active ACP session at a time and one serialized turn (<see cref="IsTurnActive"/>). Sessions map
/// 1:1 to PiSharp JSONL sessions; the ACP <c>sessionId</c> is <c>sess_</c> + the JSONL id.
/// </summary>
public sealed class AcpSessionManager
{
    public const string SessionIdPrefix = "sess_";

    private readonly SessionRuntime _runtime;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IAcpPermissionResponder? _responder;
    private string? _activeSessionId;
    private string? _activeJsonlId;
    private bool _turnActive;

    public AcpSessionManager(SessionRuntime runtime, ILoggerFactory? loggerFactory = null)
    {
        _runtime = runtime;
        _logger = loggerFactory?.CreateLogger<AcpSessionManager>() ?? NullLogger<AcpSessionManager>.Instance;
    }

    /// <summary>The underlying ACP session id, or <c>null</c> before any session is created.</summary>
    public string? ActiveSessionId { get { lock (_gate) return _activeSessionId; } }

    /// <summary>True while a <c>session/prompt</c> turn is executing (turn serialization).</summary>
    public bool IsTurnActive { get { lock (_gate) return _turnActive; } }

    /// <summary>Underlying runtime (used by the server to stream harness events).</summary>
    public SessionRuntime Runtime => _runtime;

    /// <summary>Lets the server bridge its permission responder so cancel/close release pending TCSs.</summary>
    public void AttachResponder(IAcpPermissionResponder responder) => _responder = responder;

    public async Task<AcpSessionInfo> NewAsync(string cwd, CancellationToken cancellationToken = default)
    {
        EnsureCwd(cwd);
        var result = await _runtime.NewSessionAsync(cancellationToken);
        if (result.Cancelled || result.Session is null)
            throw AcpRpcException.Server("Failed to create session");
        _logger.LogInformation("ACP session created (jsonl {JsonlId})", result.Session.Id);
        return Activate(result.Session);
    }

    public async Task<AcpSessionInfo> LoadAsync(string sessionIdOrPath, string cwd, bool replay, CancellationToken cancellationToken = default)
    {
        EnsureCwd(cwd);
        var sessions = await _runtime.ListSessionsAsync(null, cancellationToken);
        var resolved = ResolveSession(sessions, sessionIdOrPath);
        if (resolved is null)
            throw AcpRpcException.Server("session_not_found", sessionIdOrPath);
        var result = await _runtime.SwitchSessionAsync(resolved, cancellationToken);
        if (result.Cancelled || result.Session is null)
            throw AcpRpcException.Server("Failed to load session");
        _logger.LogInformation("ACP session {JsonlId} loaded (replay={Replay})", result.Session.Id, replay);
        return Activate(result.Session);
    }

    public async Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activeSessionId != sessionId)
                throw AcpRpcException.Server("session_not_found");
        }
        _runtime.Harness.Abort();
        _responder?.RejectAllPending("cancelled");
        lock (_gate)
        {
            _turnActive = false;
            _activeSessionId = null;
            _activeJsonlId = null;
        }
        _logger.LogInformation("ACP session {SessionId} closed", sessionId);
        await Task.CompletedTask;
    }

    /// <summary>Aborts the in-flight turn and releases pending permission requests (plan §4.2).</summary>
    public void Cancel(string sessionId)
    {
        _runtime.Harness.Abort();
        _responder?.RejectAllPending("cancelled");
        lock (_gate) _turnActive = false;
    }

    public async Task<AssistantMessage?> PromptAsync(string sessionId, IReadOnlyList<AcpContentBlock> prompt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_activeSessionId != sessionId)
                throw AcpRpcException.Server("no active session");
            if (_turnActive)
                throw AcpRpcException.Server("turn_in_progress");
            _turnActive = true;
        }

        try
        {
            var input = AcpContentCodec.Decode(prompt);
            return await _runtime.SubmitPromptAsync(input.Text, input.Images, "acp", cancellationToken);
        }
        finally
        {
            lock (_gate) _turnActive = false;
        }
    }

    public async Task<IReadOnlyList<AgentMessage>> BuildContextAsync(CancellationToken cancellationToken = default)
        => (await _runtime.Session.BuildContextAsync(cancellationToken)).Messages;

    private AcpSessionInfo Activate(JsonlSessionMetadata metadata)
    {
        lock (_gate)
        {
            _activeJsonlId = metadata.Id;
            _activeSessionId = SessionIdPrefix + metadata.Id;
            _turnActive = false;
        }
        return new AcpSessionInfo(_activeSessionId!, metadata.Id, metadata.Cwd);
    }

    private void EnsureCwd(string cwd)
    {
        var runtimeCwd = _runtime.Session.Metadata.Cwd;
        if (!string.Equals(Path.GetFullPath(cwd ?? string.Empty), runtimeCwd, StringComparison.Ordinal))
            throw AcpRpcException.InvalidParams("cwd must equal the process cwd (in-process ACP limitation)");
    }

    private static JsonlSessionMetadata? ResolveSession(IReadOnlyList<JsonlSessionMetadata> sessions, string sessionIdOrPath)
    {
        var target = sessionIdOrPath?.Trim();
        if (string.IsNullOrEmpty(target)) return null;
        var jsonlId = target.StartsWith(SessionIdPrefix, StringComparison.Ordinal) ? target[SessionIdPrefix.Length..] : target;
        return sessions.FirstOrDefault(s =>
            s.Id == jsonlId
            || s.Id == target
            || string.Equals(s.Path, target, StringComparison.OrdinalIgnoreCase));
    }
}
