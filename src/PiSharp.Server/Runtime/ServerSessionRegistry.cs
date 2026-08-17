using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using PiSharp.Compatibility.Settings;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using PiSharp.Server.Contracts;

namespace PiSharp.Server.Runtime;

public sealed partial class ServerSessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, LiveServerSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _liveRuntimeSessionIds = new(StringComparer.Ordinal);
    private readonly Func<CreateServerSessionRequest, CancellationToken, Task<SessionRuntimeResult>> _runtimeFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweepTask;

    public ServerSessionRegistry()
        : this(async (request, ct) => new SessionRuntimeResult(await CreateRuntimeAsync(request, ct), null))
    {
    }

    public ServerSessionRegistry(TimeSpan? idleTimeout)
        : this(async (request, ct) => new SessionRuntimeResult(await CreateRuntimeAsync(request, ct), null), idleTimeout)
    {
    }

    public ServerSessionRegistry(Func<CreateServerSessionRequest, CancellationToken, Task<SessionRuntimeResult>> runtimeFactory, TimeSpan? idleTimeout = null)
    {
        _runtimeFactory = runtimeFactory;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _sweepTask = Task.Run(SweepLoopAsync);
    }

    /// <summary>
    /// Compatibility overload: a factory that only produces the <see cref="SessionRuntime"/> (no
    /// per-session logger factory) is adapted with a null owned factory, preserving prior behavior.
    /// </summary>
    public ServerSessionRegistry(Func<CreateServerSessionRequest, CancellationToken, Task<PiSharp.Runtime.SessionRuntime>> runtimeFactory, TimeSpan? idleTimeout = null)
        : this(Wrap(runtimeFactory), idleTimeout)
    {
    }

    private static Func<CreateServerSessionRequest, CancellationToken, Task<SessionRuntimeResult>> Wrap(Func<CreateServerSessionRequest, CancellationToken, Task<PiSharp.Runtime.SessionRuntime>> runtimeFactory)
        => async (request, ct) => new SessionRuntimeResult(await runtimeFactory(request, ct), null);

    public IReadOnlyCollection<LiveServerSession> Sessions => _sessions.Values.ToArray();

    public async Task<ServerSessionCreated> CreateAsync(CreateServerSessionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCreateRequest(request);
        await RejectPersistedSessionIdCollisionAsync(request, cancellationToken);
        var serverSessionId = NewServerSessionId();
        var reservedRuntimeSessionIds = new HashSet<string>(StringComparer.Ordinal);
        PiSharp.Runtime.SessionRuntime? runtime = null;
        SessionRuntimeResult? runtimeResult = null;
        LiveServerSession? live = null;
        try
        {
            ReserveLiveRuntimeSessionId(request.SessionId, serverSessionId, reservedRuntimeSessionIds);
            runtimeResult = await _runtimeFactory(request, cancellationToken);
            runtime = runtimeResult.Runtime;
            if (!string.Equals(request.SessionId, runtime.Session.Metadata.Id, StringComparison.Ordinal)) ReserveLiveRuntimeSessionId(runtime.Session.Metadata.Id, serverSessionId, reservedRuntimeSessionIds);
            live = new LiveServerSession(serverSessionId, runtime, OnRuntimeSessionChangedAsync, loggerFactory: runtimeResult.LoggerFactory);
            var snapshot = await live.SnapshotAsync(cancellationToken);
            if (!_sessions.TryAdd(live.Id, live))
                throw new InvalidOperationException($"Server session id collision for '{live.Id}'.");

            return new ServerSessionCreated(live.Id, snapshot);
        }
        catch
        {
            foreach (var reservedRuntimeSessionId in reservedRuntimeSessionIds) ReleaseLiveRuntimeSessionId(reservedRuntimeSessionId);
            if (live is not null) await live.DisposeAsync();
            else
            {
                if (runtime is not null) await runtime.DisposeAsync();
                runtimeResult?.LoggerFactory?.Dispose();
            }
            throw;
        }
    }

    public bool TryGet(string serverSessionId, out LiveServerSession session)
        => _sessions.TryGetValue(serverSessionId, out session!);

    public async Task<bool> DisposeAsync(string serverSessionId)
    {
        if (!_sessions.TryRemove(serverSessionId, out var session)) return false;
        ReleaseLiveRuntimeSessionId(session.RuntimeSessionId);
        await session.DisposeAsync();
        return true;
    }

    public async Task<ServerSessionListResult> ListSessionsAsync(ListSessionsCommand command, CancellationToken cancellationToken = default)
    {
        var sessions = !string.IsNullOrWhiteSpace(command.ServerSessionId)
            ? await ListSessionsFromLiveSessionAsync(command, cancellationToken)
            : await ListSessionsFromRepoAsync(command, cancellationToken);

        return new ServerSessionListResult(sessions.Select(ToServerPersistedSession).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _sweepCts.Cancel();
        try { await _sweepTask; }
        catch (OperationCanceledException) when (_sweepCts.IsCancellationRequested) { }
        foreach (var id in _sessions.Keys.ToArray()) await DisposeAsync(id);
    }

    private async Task SweepLoopAsync()
    {
        var period = TimeSpan.FromMilliseconds(Math.Clamp(_idleTimeout.TotalMilliseconds / 2, 50, 1000));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(_sweepCts.Token))
            {
                var cutoff = DateTimeOffset.UtcNow - _idleTimeout;
                foreach (var session in _sessions.Values)
                {
                    if (session.AttachedClients == 0 && !session.HasPendingWork && session.LastActivityUtc < cutoff)
                    {
                        try { await DisposeAsync(session.Id); }
                        catch { /* A failing session must not stall the sweep. */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_sweepCts.IsCancellationRequested) { }
    }

    public async Task<T> RunWithReservedRuntimeSessionIdAsync<T>(LiveServerSession live, string? requestedRuntimeSessionId, Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedRuntimeSessionId)) return await operation();
        ValidateRuntimeSessionId(requestedRuntimeSessionId);
        await RejectPersistedSessionIdCollisionAsync(live.Runtime, requestedRuntimeSessionId, cancellationToken);
        ReserveLiveRuntimeSessionId(requestedRuntimeSessionId, live.Id);
        try
        {
            return await operation();
        }
        finally
        {
            if (!string.Equals(live.RuntimeSessionId, requestedRuntimeSessionId, StringComparison.Ordinal)) ReleaseLiveRuntimeSessionId(requestedRuntimeSessionId);
        }
    }

    public void EnsureRuntimeSessionSwitchAllowed(LiveServerSession live, string targetRuntimeSessionId)
    {
        if (_liveRuntimeSessionIds.TryGetValue(targetRuntimeSessionId, out var owner) && !string.Equals(owner, live.Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"Session id '{targetRuntimeSessionId}' is already live.");
    }

    private Task OnRuntimeSessionChangedAsync(LiveServerSession live, string previousRuntimeSessionId, string currentRuntimeSessionId, CancellationToken cancellationToken)
    {
        if (_liveRuntimeSessionIds.TryGetValue(currentRuntimeSessionId, out var owner))
        {
            if (!string.Equals(owner, live.Id, StringComparison.Ordinal)) throw new InvalidOperationException($"Session id '{currentRuntimeSessionId}' is already live.");
        }
        else
        {
            ReserveLiveRuntimeSessionId(currentRuntimeSessionId, live.Id);
        }
        ReleaseLiveRuntimeSessionId(previousRuntimeSessionId);
        return Task.CompletedTask;
    }

    private void ReserveLiveRuntimeSessionId(string? runtimeSessionId, string serverSessionId)
    {
        if (string.IsNullOrWhiteSpace(runtimeSessionId)) return;
        if (!_liveRuntimeSessionIds.TryAdd(runtimeSessionId, serverSessionId))
            throw new InvalidOperationException($"Session id '{runtimeSessionId}' is already live.");
    }

    private void ReserveLiveRuntimeSessionId(string? runtimeSessionId, string serverSessionId, ISet<string> reservedRuntimeSessionIds)
    {
        if (string.IsNullOrWhiteSpace(runtimeSessionId)) return;
        ReserveLiveRuntimeSessionId(runtimeSessionId, serverSessionId);
        reservedRuntimeSessionIds.Add(runtimeSessionId);
    }

    private void ReleaseLiveRuntimeSessionId(string? runtimeSessionId)
    {
        if (!string.IsNullOrWhiteSpace(runtimeSessionId)) _liveRuntimeSessionIds.TryRemove(runtimeSessionId, out _);
    }

    /// <summary>
    /// Default runtime factory mapping a <see cref="CreateServerSessionRequest"/> to
    /// <see cref="PiSharp.Runtime.SessionRuntime"/> (no telemetry).
    /// </summary>
    public static Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(CreateServerSessionRequest request, CancellationToken cancellationToken = default)
        => CreateRuntimeAsync(request, telemetry: null, cancellationToken: cancellationToken);

    /// <summary>
    /// Runtime factory with an optional daemon-side telemetry service (P25 daemon observability);
    /// the service is wired into the runtime and its harness instrumentor is bound.
    /// </summary>
    public static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(CreateServerSessionRequest request, PiSharp.Runtime.Telemetry.TelemetryService? telemetry, CancellationToken cancellationToken, ILoggerFactory? loggerFactory = null)
        => await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(request.Cwd),
            SessionsRoot: request.SessionsRoot,
            Model: new RuntimeModelOptions(request.Provider, request.Model, request.Thinking, request.ScopedModels),
            Tools: new RuntimeToolOptions(request.Tools, request.NoTools, request.NoBuiltinTools),
            Resources: new RuntimeResourceOptions(request.Extensions, DisableExtensions: request.NoExtensions, DisableTypeScriptExtensions: request.NoTsExtensions, DisableSkills: request.NoSkills, DisablePromptTemplates: request.NoPromptTemplates, DisableThemes: request.NoThemes, DisableContextFiles: request.NoContextFiles),
            Session: new RuntimeSessionStartupOptions(SessionIdOrPath: request.SessionIdOrPath, ContinueLatestForCwd: request.ContinueLatestForCwd, NewSessionId: request.SessionId),
            Telemetry: telemetry),
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);

    private static void ValidateCreateRequest(CreateServerSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Cwd)) throw new ArgumentException("cwd is required.", nameof(request));
        ValidateRuntimeSessionId(request.SessionId);
        if (!string.IsNullOrWhiteSpace(request.SessionId) && (!string.IsNullOrWhiteSpace(request.SessionIdOrPath) || request.ContinueLatestForCwd))
            throw new ArgumentException("sessionId can only be provided when creating a new runtime session.", nameof(request));
    }

    private static void ValidateRuntimeSessionId(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && !SessionIdPattern().IsMatch(sessionId))
            throw new ArgumentException("sessionId may contain only letters, numbers, '.', '_' and '-'.", nameof(sessionId));
    }

    private async Task<IReadOnlyList<JsonlSessionMetadata>> ListSessionsFromLiveSessionAsync(ListSessionsCommand command, CancellationToken cancellationToken)
    {
        if (!TryGet(command.ServerSessionId!, out var live))
            throw new InvalidOperationException($"Server session '{command.ServerSessionId}' not found.");

        return await live.RunExclusiveAsync((runtime, token) =>
        {
            var cwd = command.AllCwds ? null : command.Cwd ?? runtime.Session.Metadata.Cwd;
            return runtime.ListSessionsAsync(cwd, token);
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<JsonlSessionMetadata>> ListSessionsFromRepoAsync(ListSessionsCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Cwd))
            throw new InvalidOperationException("cwd is required when serverSessionId is not provided.");

        var repo = await CreateSessionRepoAsync(command.Cwd, command.SessionsRoot, cancellationToken);
        return await repo.ListAsync(new JsonlSessionListOptions(command.AllCwds ? null : command.Cwd), cancellationToken);
    }

    private ServerPersistedSession ToServerPersistedSession(JsonlSessionMetadata session)
    {
        _liveRuntimeSessionIds.TryGetValue(session.Id, out var serverSessionId);
        return new ServerPersistedSession(
            session.Id,
            session.CreatedAt,
            session.Cwd,
            session.Path,
            session.ParentSessionPath,
            serverSessionId is not null,
            serverSessionId);
    }

    private static async Task<JsonlSessionRepo> CreateSessionRepoAsync(string cwd, string? sessionsRoot, CancellationToken cancellationToken)
    {
        var env = new SystemExecutionEnv(cwd);
        var settings = await new PiSettingsStore().LoadAsync(env.Cwd, cancellationToken: cancellationToken);
        var sessionRoot = sessionsRoot ?? settings.Settings.SessionDir ?? settings.Paths.SessionsRoot;
        return new JsonlSessionRepo(env, sessionRoot);
    }

    private static async Task RejectPersistedSessionIdCollisionAsync(CreateServerSessionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) return;
        var repo = await CreateSessionRepoAsync(request.Cwd, request.SessionsRoot, cancellationToken);
        var sessions = await repo.ListAsync(null, cancellationToken);
        if (sessions.Any(session => string.Equals(session.Id, request.SessionId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Session id '{request.SessionId}' already exists.");
    }

    private static async Task RejectPersistedSessionIdCollisionAsync(PiSharp.Runtime.SessionRuntime runtime, string requestedRuntimeSessionId, CancellationToken cancellationToken)
    {
        var sessions = await runtime.ListSessionsAsync(null, cancellationToken);
        if (sessions.Any(session => string.Equals(session.Id, requestedRuntimeSessionId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Session id '{requestedRuntimeSessionId}' already exists.");
    }

    private static string NewServerSessionId() => "srv_" + Guid.CreateVersion7().ToString("N");

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex SessionIdPattern();
}
