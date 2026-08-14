using PiSharp.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
var apiKey = builder.Configuration["PiSharp:Server:ApiKey"] ?? Environment.GetEnvironmentVariable("PISHARP_SERVER_API_KEY");
var host = new PiServerHost(new PiServerHostOptions { ApiKey = apiKey ?? string.Empty });
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
