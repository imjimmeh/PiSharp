using Microsoft.Extensions.Logging;
using PiSharp.Logging;
using PiSharp.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["PiSharp:Server:ApiKey"] ?? Environment.GetEnvironmentVariable("PISHARP_SERVER_API_KEY");

// Standalone daemon: write lifecycle diagnostics to ~/.pi/PiSharp/logs/daemon/pi.log. The CLI-hosted
// path injects its own factory; keep ASP.NET defaults intact (PiServerHost adds the Microsoft filter).
using var loggerFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Debug);
    CliFileLogging.AddConfiguredFileLogging(b, Directory.GetCurrentDirectory(), context: LogContext.Daemon);
});

var host = new PiServerHost(new PiServerHostOptions { ApiKey = apiKey ?? string.Empty, LoggerFactory = loggerFactory, PerSessionFileLogging = true });
await host.StartAsync();

var cts = new CancellationTokenSource();
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
