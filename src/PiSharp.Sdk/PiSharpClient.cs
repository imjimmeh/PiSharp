using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PiSharp.Client;
using PiSharp.Compatibility.Settings;
using PiSharp.Logging;
using PiSharp.Server.Contracts;

namespace PiSharp.Sdk;

/// <summary>Result of <see cref="PiSharpClient.DaemonStatusAsync"/> (health + lease snapshot).</summary>
public sealed record DaemonStatusResult(
    bool Available,
    int Pid,
    int Port,
    string? RuntimeVersion,
    string ProtocolVersion);

/// <summary>
/// Options for <see cref="PiSharpClient.CreateSessionAsync"/>, mirroring the daemon's
/// <c>create_session</c> request shape.
/// </summary>
public sealed record CreateSessionOptions(
    string Cwd,
    string? SessionId = null,
    string? SessionIdOrPath = null,
    bool ContinueLatestForCwd = false,
    string? SessionsRoot = null,
    string? Provider = null,
    string? Model = null,
    PiSharp.Abstractions.Options.ThinkingLevel? Thinking = null,
    IReadOnlyList<string>? ScopedModels = null,
    IReadOnlyList<string>? Tools = null,
    bool NoTools = false,
    bool NoBuiltinTools = false,
    IReadOnlyList<string>? Extensions = null,
    bool NoExtensions = false,
    bool NoSkills = false,
    bool NoPromptTemplates = false,
    bool NoThemes = false,
    bool NoContextFiles = false);

/// <summary>
/// Daemon-client SDK entry point: resolves (or starts) the daemon via the P01 lease machinery,
/// opens the control WebSocket, and exposes session lifecycle commands. Disposing the client
/// detaches its sockets but does not stop the daemon.
/// </summary>
public sealed class PiSharpClient : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly DaemonLease _lease;
    private readonly ClientWebSocketTransport _transport;
    private readonly ClientSessionConnection _connection;
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _ownsLoggerFactory;
    private readonly List<SessionConnection> _attached = [];
    private readonly object _sync = new();
    private int _disposed;

    private PiSharpClient(DaemonLease lease, ClientWebSocketTransport transport, ClientSessionConnection connection, ILoggerFactory loggerFactory, bool ownsLoggerFactory)
    {
        _lease = lease;
        _transport = transport;
        _connection = connection;
        _loggerFactory = loggerFactory;
        _ownsLoggerFactory = ownsLoggerFactory;
    }

    /// <summary>The resolved P01 daemon lease (pid, port, api key, runtime version).</summary>
    public DaemonLease Lease => _lease;

    /// <summary>The daemon protocol version this SDK speaks (see <see cref="PiSharpSdk.ProtocolVersion"/>).</summary>
    public string ServerProtocolVersion => PiSharpSdk.ProtocolVersion;

    /// <summary>The daemon is always local (localhost-only), so this is always true.</summary>
    public bool IsLocalHost => true;

    /// <summary>
    /// Resolves a compatible daemon lease (auto-starting one via <see cref="DaemonLauncher"/> when
    /// <see cref="PiSharpClientOptions.AutoStartDaemon"/> is set and no lease exists), opens the
    /// control WebSocket, and returns a connected <see cref="PiSharpClient"/>.
    /// </summary>
    /// <exception cref="SdkException">No compatible daemon is reachable and none could be started.</exception>
    public static async Task<PiSharpClient> ConnectAsync(
        PiSharpClientOptions options,
        CancellationToken ct = default,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cwd = options.Cwd ?? Directory.GetCurrentDirectory();
        var ownsLoggerFactory = loggerFactory is null;
        loggerFactory ??= BuildFileLoggerFactory(cwd);
        var leaseDirectory = options.LeaseDirectory ?? PiAgentPaths.FromCwd(cwd).GlobalPiSharpDirectory;
        var store = new DaemonLeaseStore(leaseDirectory);

        var lease = await store.ReadAsync(ct);
        if (lease is not null && !IsRuntimeCompatible(lease.Version)) lease = null;

        var (port, apiKey) = await ResolveEndpointAsync(options, store, lease, cwd, ct);
        if (port is null || apiKey is null)
        {
            throw new SdkException(
                $"No compatible daemon is running (lease directory: {leaseDirectory}). " +
                "Start one with 'pi daemon start', or set AutoStartDaemon.");
        }

        // Align the lease with the effective endpoint so attached sockets and status reports use
        // the port/api key actually connected (option overrides win over the stored lease).
        if (lease is null)
        {
            lease = new DaemonLease(0, port.Value, apiKey, DateTimeOffset.UtcNow, RuntimeVersion());
        }
        else if (lease.Port != port.Value || lease.ApiKey != apiKey)
        {
            lease = lease with { Port = port.Value, ApiKey = apiKey };
        }

        if (!await IsHealthyAsync(port.Value, options.EffectiveConnectTimeout, ct))
        {
            throw new SdkException($"Daemon at 127.0.0.1:{port.Value} is not responding to /health.");
        }

        var transport = new ClientWebSocketTransport(loggerFactory.CreateLogger<ClientWebSocketTransport>(), CommandTimeout);
        var connection = new ClientSessionConnection(transport, loggerFactory.CreateLogger<ClientSessionConnection>());
        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{port.Value}"), apiKey, ct);
        return new PiSharpClient(lease, transport, connection, loggerFactory, ownsLoggerFactory);
    }

    /// <summary>Creates a daemon session with the given options.</summary>
    public async Task<ServerSessionCreated> CreateSessionAsync(CreateSessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await _connection.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession),
            CreateSessionPayload(options),
            ct);
        ThrowOnFailure(response, "create_session");
        return FromServerPayload<ServerSessionCreated>(response.Data)
            ?? throw new InvalidOperationException("create_session returned no session.");
    }

    public async Task<ServerSessionListResult> ListSessionsAsync(string? cwd = null, string? sessionsRoot = null, CancellationToken ct = default)
    {
        var response = await _connection.SendAsync(
            new ServerCommandEnvelope(ServerCommandTypes.ListSessions),
            new ListSessionsCommand(ServerCommandTypes.ListSessions, Cwd: cwd, SessionsRoot: sessionsRoot),
            ct);
        ThrowOnFailure(response, "list_sessions");
        return FromServerPayload<ServerSessionListResult>(response.Data)
            ?? new ServerSessionListResult([]);
    }
    /// and returns a <see cref="SessionConnection"/> that replays the retained event log and then
    /// streams live events. The returned connection is independent of the client's control socket;
    /// disposing it detaches without stopping the daemon.
    /// </summary>
    public async Task<SessionConnection> AttachAsync(
        string serverSessionId,
        AttachOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverSessionId);
        options ??= new AttachOptions();

        var transport = new ClientWebSocketTransport(_loggerFactory.CreateLogger<ClientWebSocketTransport>(), CommandTimeout);
        var connection = new ClientSessionConnection(transport, _loggerFactory.CreateLogger<ClientSessionConnection>());
        try
        {
            await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{_lease.Port}"), _lease.ApiKey, ct);
            var session = await SessionConnection.CreateAsync(connection, serverSessionId, options, ct);
            lock (_sync)
            {
                if (_disposed == 0) _attached.Add(session);
            }

            return session;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Reports daemon availability by polling <c>/health</c> and checking the lease pid. No new
    /// daemon command is used (the merged daemon surface has no <c>daemon_status</c> command yet).
    /// </summary>
    public async Task<DaemonStatusResult> DaemonStatusAsync(CancellationToken ct = default)
    {
        var available = await IsHealthyAsync(_lease.Port, TimeSpan.FromSeconds(2), ct);
        return new DaemonStatusResult(
            available,
            _lease.Pid,
            _lease.Port,
            _lease.Version,
            PiSharpSdk.ProtocolVersion);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SessionConnection[] attached;
        lock (_sync)
        {
            attached = _attached.ToArray();
            _attached.Clear();
        }

        foreach (var session in attached)
        {
            await session.DisposeAsync();
        }

        await _connection.DisposeAsync();

        if (_ownsLoggerFactory)
        {
            _loggerFactory.Dispose();
        }
    }

    // --- endpoint resolution ---

    private static async Task<(int? Port, string? ApiKey)> ResolveEndpointAsync(
        PiSharpClientOptions options,
        DaemonLeaseStore store,
        DaemonLease? lease,
        string cwd,
        CancellationToken ct)
    {
        // Explicit overrides win; a lease supplies the defaults.
        var port = options.Port ?? lease?.Port;
        var apiKey = options.ApiKey ?? lease?.ApiKey;

        if (port is not null && apiKey is not null)
        {
            return (port, apiKey);
        }

        if (!options.AutoStartDaemon)
        {
            return (port, apiKey);
        }

        port ??= PickFreePort();
        apiKey ??= Guid.NewGuid().ToString("N");
        var executable = options.DaemonExecutable ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new SdkException("Cannot auto-start the daemon: no executable resolved. Set PiSharpClientOptions.DaemonExecutable.");
        }

        var launcher = new DaemonLauncher(store);
        var started = await launcher.StartDaemonAsync(
            executable,
            $"daemon foreground --port {port.Value} --api-key {apiKey}",
            port.Value,
            apiKey,
            RuntimeVersion(),
            ct);
        if (started is null)
        {
            throw new SdkException($"Failed to start the daemon on 127.0.0.1:{port.Value}: health check timed out.");
        }

        return (started.Port, started.ApiKey);
    }

    private static async Task<bool> IsHealthyAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (response.StatusCode == HttpStatusCode.OK) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    private static bool IsRuntimeCompatible(string leaseVersion)
    {
        if (!Version.TryParse(leaseVersion, out var lease)) return false;
        return lease.Major == Environment.Version.Major && lease.Minor == Environment.Version.Minor;
    }

    private static string RuntimeVersion() => $"{Environment.Version.Major}.{Environment.Version.Minor}";

    /// <summary>
    /// Fallback file-logging factory for hosts that pass no <see cref="ILoggerFactory"/>: the SDK
    /// still writes structured logs to the shared <c>~/.pi/PiSharp/logs</c> directory instead of
    /// silently dropping diagnostics.
    /// </summary>
    private static ILoggerFactory BuildFileLoggerFactory(string cwd)
        => LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            CliFileLogging.AddConfiguredFileLogging(builder, cwd);
        });

    private static int PickFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static object CreateSessionPayload(CreateSessionOptions options) => new
    {
        cwd = options.Cwd,
        sessionId = options.SessionId,
        sessionIdOrPath = options.SessionIdOrPath,
        continueLatestForCwd = options.ContinueLatestForCwd,
        sessionsRoot = options.SessionsRoot,
        provider = options.Provider,
        model = options.Model,
        thinking = options.Thinking,
        scopedModels = options.ScopedModels,
        tools = options.Tools,
        noTools = options.NoTools,
        noBuiltinTools = options.NoBuiltinTools,
        extensions = options.Extensions,
        noExtensions = options.NoExtensions,
        noSkills = options.NoSkills,
        noPromptTemplates = options.NoPromptTemplates,
        noThemes = options.NoThemes,
        noContextFiles = options.NoContextFiles,
    };

    private static T? FromServerPayload<T>(object? data)
    {
        if (data is null) return default;
        return System.Text.Json.JsonSerializer.Deserialize<T>(
            System.Text.Json.JsonSerializer.Serialize(data, PiSharp.Server.Serialization.ServerJsonSerializer.Options),
            PiSharp.Server.Serialization.ServerJsonSerializer.Options);
    }

    private static void ThrowOnFailure(ServerResponse response, string command)
    {
        if (!response.Success)
        {
            throw new SdkException($"Command '{command}' failed: {response.Error?.Code}: {response.Error?.Message}");
        }
    }
}
