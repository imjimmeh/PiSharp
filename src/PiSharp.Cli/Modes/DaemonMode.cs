using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using PiSharp.Cli.IO;
using PiSharp.Cli.Logging;
using PiSharp.Cli.Parsing;
using PiSharp.Client;
using PiSharp.Compatibility.Settings;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;

namespace PiSharp.Cli.Modes;

public static class DaemonMode
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(
        DaemonCommandArgs command,
        IConsoleIO console,
        CancellationToken cancellationToken = default,
        string? leaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(console);

        var store = new DaemonLeaseStore(leaseDirectory ?? PiAgentPaths.FromCwd(Directory.GetCurrentDirectory()).GlobalPiSharpDirectory);

        return command.Kind switch
        {
            DaemonCommandKind.Start => await StartAsync(command, console, store, cancellationToken),
            DaemonCommandKind.Stop => await StopAsync(console, store, cancellationToken),
            DaemonCommandKind.Status => await StatusAsync(console, store, cancellationToken),
            _ => 2
        };
    }

    private static async Task<int> StartAsync(DaemonCommandArgs command, IConsoleIO console, DaemonLeaseStore store, CancellationToken cancellationToken)
    {
        var port = ResolvePort(command.Port);
        if (port is null)
        {
            await console.Error.WriteLineAsync("invalid port".AsMemory(), cancellationToken);
            return 1;
        }

        var apiKey = command.ApiKey ?? Guid.NewGuid().ToString("N");
        var version = $"{Environment.Version.Major}.{Environment.Version.Minor}";

        using var startLock = DaemonLock.TryAcquire(store.LockPath);
        if (startLock is null)
        {
            await console.Error.WriteLineAsync("daemon already running".AsMemory(), cancellationToken);
            return 1;
        }

        if (command.Foreground)
        {
            return await RunForegroundAsync(console, store, port.Value, apiKey, version, cancellationToken);
        }

        var launcher = new DaemonLauncher(store);
        var lease = await launcher.StartDaemonAsync(
            executable: Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the current executable path."),
            arguments: $"daemon foreground --port {port} --api-key {apiKey}",
            port.Value,
            apiKey,
            version,
            cancellationToken,
            startLock);

        if (lease is null)
        {
            await console.Error.WriteLineAsync("failed to start daemon: health check timed out".AsMemory(), cancellationToken);
            return 1;
        }

        await console.Out.WriteLineAsync($"daemon started (pid {lease.Pid}) on http://127.0.0.1:{lease.Port}".AsMemory(), cancellationToken);
        return 0;
    }

    private static async Task<int> RunForegroundAsync(IConsoleIO console, DaemonLeaseStore store, int port, string apiKey, string version, CancellationToken cancellationToken)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddDebug();
            _ = CliFileLogging.AddConfiguredFileLogging(builder, Directory.GetCurrentDirectory());
        });
        PiServerHost? host = null;
        var hostOptions = DaemonCommandHost.CreateHostOptions(
            apiKey,
            loggerFactory,
            resolveSession: () => host?.Registry?.Sessions.MaxBy(s => s.Id, StringComparer.Ordinal));
        host = new PiServerHost(hostOptions);
        await host.StartAsync(port);
        await store.WriteAsync(new DaemonLease(Environment.ProcessId, host.Port, apiKey, DateTimeOffset.UtcNow, version), cancellationToken);
        await console.Out.WriteLineAsync($"daemon listening on http://127.0.0.1:{host.Port}".AsMemory(), cancellationToken);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, host.ShutdownToken);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await host.StopAsync();
        return 0;
    }

    private static async Task<int> StopAsync(IConsoleIO console, DaemonLeaseStore store, CancellationToken cancellationToken)
    {
        var lease = await store.TryReadAsync(cancellationToken);
        if (lease is null)
        {
            await console.Error.WriteLineAsync("No daemon running.".AsMemory(), cancellationToken);
            return 1;
        }

        var stopped = false;
        try
        {
            await using var transport = new ClientWebSocketTransport();
            await transport.ConnectAsync(new Uri($"ws://127.0.0.1:{lease.Port}"), lease.ApiKey, cancellationToken);
            var response = await transport.SendCommandAsync(new ServerCommandEnvelope(ServerCommandTypes.Shutdown, Id: Guid.NewGuid().ToString("N")), cancellationToken);
            stopped = response.Success;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or OperationCanceledException)
        {
            stopped = false; // daemon is not reachable — treat the leftover lease as dead
        }

        await store.ClearAsync(cancellationToken);

        if (stopped)
        {
            await console.Out.WriteLineAsync("Daemon stopped.".AsMemory(), cancellationToken);
            return 0;
        }

        await console.Error.WriteLineAsync("daemon is not running".AsMemory(), cancellationToken);
        return 1;
    }

    private static async Task<int> StatusAsync(IConsoleIO console, DaemonLeaseStore store, CancellationToken cancellationToken)
    {
        var lease = await store.TryReadAsync(cancellationToken);
        if (lease is null)
        {
            await console.Error.WriteLineAsync("No daemon running.".AsMemory(), cancellationToken);
            return 1;
        }

        var alive = DaemonLeaseStore.ProcessAlive(lease.Pid);
        await console.Out.WriteLineAsync($"http://127.0.0.1:{lease.Port} pid={lease.Pid} {(alive ? "alive" : "dead")}".AsMemory(), cancellationToken);
        return alive ? 0 : 1;
    }

    /// <summary>
    /// Starts a foreground daemon on a free port using the same port/api-key/version logic as
    /// <see cref="StartAsync"/>, for the interactive mode's remote path. When another process holds
    /// the start lock, someone else is already starting or running the daemon: the stored lease is
    /// returned as-is (null when it is not readable or the pid is dead).
    /// </summary>
    internal static async Task<DaemonLease?> TryAutoStartAsync(DaemonLeaseStore store, CancellationToken cancellationToken)
    {
        var port = PickFreePort();
        var apiKey = Guid.NewGuid().ToString("N");
        var version = $"{Environment.Version.Major}.{Environment.Version.Minor}";

        using var startLock = DaemonLock.TryAcquire(store.LockPath);
        if (startLock is null)
        {
            return await store.ReadAsync(cancellationToken);
        }

        var launcher = new DaemonLauncher(store);
        return await launcher.StartDaemonAsync(
            executable: Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the current executable path."),
            arguments: $"daemon foreground --port {port} --api-key {apiKey}",
            port,
            apiKey,
            version,
            cancellationToken,
            startLock);
    }

    private static int? ResolvePort(string? portValue)
    {
        if (portValue is null) return PickFreePort();
        return int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
    }

    private static int PickFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal sealed class DaemonLock(string path) : IDisposable
    {
        private readonly FileStream _stream = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

        public static DaemonLock? TryAcquire(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                return new DaemonLock(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
