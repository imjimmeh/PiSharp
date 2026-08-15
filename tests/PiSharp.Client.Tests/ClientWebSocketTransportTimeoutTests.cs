using Microsoft.AspNetCore.Hosting;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;
using Xunit;

namespace PiSharp.Client.Tests;

/// <summary>
/// Per-command timeout override on the real <see cref="ClientWebSocketTransport"/> send path,
/// proven against a loopback WebSocket server that deliberately delays every response
/// (simulating slow server-side work such as extension discovery during create_session).
/// Short durations keep the tests fast: default 300ms vs override 2s vs server delay 1s.
/// </summary>
public sealed class ClientWebSocketTransportTimeoutTests
{
    [Fact]
    public async Task CreateSession_WithTimeoutOverride_SucceedsBeyondDefault_AndOtherCommandsKeepDefault()
    {
        await using var server = await DelayedResponseServer.StartAsync(delay: TimeSpan.FromSeconds(1));
        await using var transport = new ClientWebSocketTransport(TimeSpan.FromMilliseconds(300));

        await transport.ConnectAsync(server.WsUri, "test-key", CancellationToken.None);

        // create_session response arrives after 1s — far beyond the 300ms default — so only
        // the per-command override (2s) lets it through.
        var stopwatch = Stopwatch.StartNew();
        var create = await transport.SendCommandAsync(
            new ServerCommandEnvelope(ServerCommandTypes.CreateSession, Id: Guid.NewGuid().ToString("N")),
            payload: new { cwd = @"C:\work" },
            CancellationToken.None,
            timeoutOverride: TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(create.Success, $"create_session with override should succeed; got {create.Error?.Code}: {create.Error?.Message}");
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800),
            $"override should have waited out the server delay; elapsed {stopwatch.Elapsed.TotalSeconds:0.###}s");

        // A subsequent command with no override still applies the transport default and times out.
        var plain = await transport.SendCommandAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetState, Id: Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.False(plain.Success);
        Assert.Equal("timeout", plain.Error?.Code);
    }

    [Fact]
    public async Task CommandWithoutOverride_TimesOutAtTransportDefault()
    {
        await using var server = await DelayedResponseServer.StartAsync(delay: TimeSpan.FromSeconds(1));
        await using var transport = new ClientWebSocketTransport(TimeSpan.FromMilliseconds(300));

        await transport.ConnectAsync(server.WsUri, "test-key", CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var response = await transport.SendCommandAsync(
            new ServerCommandEnvelope(ServerCommandTypes.GetState, Id: Guid.NewGuid().ToString("N")),
            CancellationToken.None);
        stopwatch.Stop();

        Assert.False(response.Success);
        Assert.Equal("timeout", response.Error?.Code);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(900),
            $"default timeout should fire near 300ms, not wait for the server; elapsed {stopwatch.Elapsed.TotalSeconds:0.###}s");
    }

    /// <summary>
    /// Minimal Kestrel WebSocket endpoint at /ws that echoes an Ok response for every command,
    /// after a fixed delay — simulating a slow daemon.
    /// </summary>
    private sealed class DelayedResponseServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private DelayedResponseServer(WebApplication app, Uri wsUri)
        {
            _app = app;
            WsUri = wsUri;
        }

        /// <summary>Bare ws://127.0.0.1:port endpoint (path "/"); the transport rewrites it to /ws.</summary>
        public Uri WsUri { get; }

        public static async Task<DelayedResponseServer> StartAsync(TimeSpan delay)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            app.Map("/ws", (HttpContext context) => ServeAsync(context, delay));
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            var wsUri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal));
            return new DelayedResponseServer(app, wsUri);
        }

        private static async Task ServeAsync(HttpContext context, TimeSpan delay)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var frame = await ReceiveTextAsync(socket, context.RequestAborted);
                if (frame is null) break; // client closed

                using var document = JsonDocument.Parse(frame);
                var id = document.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "unknown";

                await Task.Delay(delay, context.RequestAborted);

                var response = ServerResponse.Ok(id, type);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ServerJsonSerializer.Options);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
            }
        }

        private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[16 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
