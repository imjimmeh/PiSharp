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

    public int Port { get; private set; }

    /// <summary>Fires when <see cref="StopAsync"/> is invoked. The foreground daemon waits on this to exit.</summary>
    public CancellationToken ShutdownToken => _stopCts.Token;

    public async Task StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddSingleton(new ApiKeyValidator(new ApiKeyOptions { ApiKey = options.ApiKey }));
        builder.Services.AddSingleton(new ServerSessionRegistry(idleTimeout: options.IdleTimeout));
        builder.Services.AddSingleton(new PiServerCommandDelegates(
            options.RunCommandAsync,
            options.CompleteCommandAsync,
            options.ProcessInputAsync,
            options.GetStartupMessagesAsync,
            options.PostStartupChecksAsync,
            OnShutdown: _ => StopAsync()));
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
