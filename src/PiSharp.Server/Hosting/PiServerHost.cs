using PiSharp.Logging;
using PiSharp.Extensions;
using PiSharp.Server.Contracts;
using PiSharp.Server.UiBridge;

using PiSharp.Server.Authentication;
using PiSharp.Server.Runtime;
using PiSharp.Server.WebSockets;

namespace PiSharp.Server.Hosting;
public sealed class PiServerHost(PiServerHostOptions options) : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly CancellationTokenSource _stopCts = new();

    /// <summary>
    /// The daemon-shared telemetry aggregator backing <c>get_metrics</c>. Always present; reports
    /// <see cref="MetricsSnapshot.Disabled"/> when <see cref="PiServerHostOptions.TelemetryEnabled"/> is false.
    /// </summary>
    public TelemetryMetricsAggregator Metrics { get; } = new(options.TelemetryEnabled);

    public int Port { get; private set; }

    /// <summary>Fires when <see cref="StopAsync"/> is invoked. The foreground daemon waits on this to exit.</summary>
    public CancellationToken ShutdownToken => _stopCts.Token;

    /// <summary>
    /// The session registry built by <see cref="StartAsync"/> and registered as a DI singleton.
    /// Assigned once a host is started; used by the daemon launcher to resolve the active session
    /// for the <c>process_input</c> lane. Null until <see cref="StartAsync"/> completes.
    /// </summary>
    public ServerSessionRegistry Registry { get; private set; } = null!;

    public async Task StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        if (options.LoggerFactory is not null) builder.Services.AddSingleton(options.LoggerFactory);
        builder.Services.AddSingleton(new ApiKeyValidator(new ApiKeyOptions { ApiKey = options.ApiKey }));
        var metricsAggregator = Metrics;
        builder.Services.AddSingleton(metricsAggregator);
        builder.Services.AddSingleton(options);
        var registry = new ServerSessionRegistry(CreateRuntimeFactory(options, Metrics), options.IdleTimeout);
        Registry = registry;
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(new PiServerCommandDelegates(
            options.RunCommandAsync,
            options.CompleteCommandAsync,
            options.ProcessInputAsync,
            options.GetStartupMessagesAsync,
            options.PostStartupChecksAsync,
            GetCommandsAsync: options.GetCommandsAsync,
            GetMcpStatusAsync: options.GetMcpStatusAsync,
            OnShutdown: _ => StopAsync()));
        builder.Services.AddSingleton(new ThemeRegistry());
        builder.Services.AddSingleton<IServerUiBridge, ServerUiBridge>();
        builder.Services.AddSingleton<PiServerWebSocketHandler>();
        builder.Services.AddSingleton<IHostedService>(_ => new StopOnShutdownService(_stopCts.Token, StopAsync));

        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        _app.Map("/ws", async (HttpContext context, PiServerWebSocketHandler handler) => await handler.HandleHttpAsync(context));
        await _app.StartAsync();
        Port = _app.Urls.Select(ParsePort).First();
    }

    public async Task StopAsync()
    {
        _stopCts.Cancel();
        await (_app?.StopAsync() ?? Task.CompletedTask);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopCts.Dispose();
    }

    private static int ParsePort(string url) => new Uri(url).Port;

    /// <summary>
    /// Runtime factory used by the host's registry. With telemetry disabled the default
    /// telemetry-free factory is used; with telemetry enabled each runtime receives its own
    /// <see cref="PiSharp.Runtime.Telemetry.TelemetryService"/> feeding the shared aggregator
    /// (plus any host-configured sinks), and its harness instrumentor is bound. When
    /// <see cref="PiServerHostOptions.PerSessionFileLogging"/> is set, the runtime's
    /// <see cref="ILoggerFactory"/> is a session-scoped fan-out that also writes to
    /// <c>logs/daemon/&lt;cwd&gt;/&lt;session&gt;.log</c>; the owned session factory travels with the
    /// returned <see cref="SessionRuntimeResult"/> so the hosting <see cref="LiveServerSession"/>
    /// disposes it at session teardown.
    /// </summary>
    private static Func<CreateServerSessionRequest, CancellationToken, Task<SessionRuntimeResult>> CreateRuntimeFactory(PiServerHostOptions options, TelemetryMetricsAggregator metrics)
        => async (request, cancellationToken) =>
        {
            PiSharp.Runtime.Telemetry.TelemetryService? telemetry = null;
            if (options.TelemetryEnabled)
            {
                var sinks = new List<ITelemetrySink> { metrics };
                if (options.TelemetrySinks is not null) sinks.AddRange(options.TelemetrySinks);
                telemetry = new PiSharp.Runtime.Telemetry.TelemetryService(enabled: true, sinks: sinks);
            }

            if (!options.PerSessionFileLogging || options.LoggerFactory is null)
            {
                var plainRuntime = await ServerSessionRegistry.CreateRuntimeAsync(
                    request,
                    telemetry,
                    cancellationToken,
                    loggerFactory: options.LoggerFactory);
                return new SessionRuntimeResult(plainRuntime, LoggerFactory: null);
            }

            var sessionLogging = CliFileLogging.CreateConfiguredFileLogging(request.Cwd, homeDirectory: options.LogHomeDirectory, context: LogContext.Daemon);
            ILoggerFactory? sessionFileFactory = null;
            if (sessionLogging is not null)
            {
                sessionFileFactory = LoggerFactory.Create(builder =>
                {
                    builder.SetMinimumLevel(LogLevel.Debug);
                    builder.AddDebug();
                    builder.AddProvider(sessionLogging.Provider);
                });
            }

            sessionLogging?.SetLogFolderPath(request.Cwd);
            var sessionFactory = new SessionLoggerFactory(options.LoggerFactory, sessionFileFactory);
            var runtime = await ServerSessionRegistry.CreateRuntimeAsync(
                request,
                telemetry,
                cancellationToken,
                loggerFactory: sessionFactory);
            sessionLogging?.SetSessionPath(runtime.Session.Metadata.Path);
            if (sessionFactory is not null)
            {
                sessionFactory.CreateLogger("PiSharp.Daemon.Session").LogInformation(
                    "daemon session in folder cwd={Cwd} sessionPath={SessionPath}",
                    runtime.Session.Metadata.Cwd,
                    runtime.Session.Metadata.Path);
            }
            return new SessionRuntimeResult(runtime, sessionFileFactory is null ? null : sessionFactory);
        };

    /// <summary>Stops the Kestrel host when the shutdown token fires, so cancellation alone triggers graceful teardown.</summary>
    private sealed class StopOnShutdownService(CancellationToken shutdown, Func<Task> stopHost) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, shutdown);
                }
                catch (OperationCanceledException)
                {
                }

                try
                {
                    await stopHost();
                }
                catch (Exception)
                {
                    // Best-effort: the host is already stopping via StopAsync/DisposeAsync.
                }
            }, CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
