using PiSharp.Server.Authentication;
using PiSharp.Server.Runtime;
using PiSharp.Server.WebSockets;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<ServerSessionRegistry>();
builder.Services.AddSingleton<PiServerWebSocketHandler>();

var app = builder.Build();

app.UseWebSockets();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Map("/ws", async (HttpContext context, PiServerWebSocketHandler handler) => await handler.HandleHttpAsync(context));

app.Run();
