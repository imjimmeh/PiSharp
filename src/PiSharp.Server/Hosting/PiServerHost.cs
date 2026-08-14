using PiSharp.Server.Contracts;
using PiSharp.Server.UiBridge;

using PiSharp.Server.Authentication;
using PiSharp.Server.Runtime;
using PiSharp.Server.WebSockets;

namespace PiSharp.Server.Hosting;

public sealed class PiServerHost(PiServerHostOptions options) : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }

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
            options.PostStartupChecksAsync));
        builder.Services.AddSingleton<IServerUiBridge, ServerUiBridge>();
        builder.Services.AddSingleton<PiServerWebSocketHandler>();

        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        _app.Map("/ws", async (HttpContext context, PiServerWebSocketHandler handler) => await handler.HandleHttpAsync(context));
        await _app.StartAsync();
        Port = _app.Urls.Select(ParsePort).First();
    }

    public async Task StopAsync() => await (_app?.StopAsync() ?? Task.CompletedTask);

    public async ValueTask DisposeAsync() => await StopAsync();

    private static int ParsePort(string url) => new Uri(url).Port;
}
