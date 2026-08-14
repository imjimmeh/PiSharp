using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;
using Xunit;

namespace PiSharp.Plugins.Lsp.Tests;

/// <summary>
/// Transport-level tests: Content-Length framing round-trips, pending-map correlation
/// (including out-of-order responses), notifications, canned inbound responses, and
/// pump-close faulting. All traffic flows through the in-memory fake process pipes — no
/// real processes.
/// </summary>
public sealed class FramedTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestRoundTripsOverContentLengthFrames()
    {
        var harness = CreateHarness(handler: null, answerAll: true);
        try
        {
            var result = await harness.Connection.RequestAsync("echo", new { value = 42 }, CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(42, result.GetProperty("echoed").GetProperty("value").GetInt32());

            var received = Assert.Single(harness.Server.Received);
            Assert.Equal("echo", received.MethodOrCommand);
            Assert.Equal(42, received.ParamsOrArguments.GetProperty("value").GetInt32());
            Assert.False(received.IsNotification);
            Assert.True(received.IdOrSeq > 0);
        }
        finally
        {
            await harness.CleanupAsync();
        }
    }

    [Fact]
    public async Task OutOfOrderResponsesCorrelateByPendingId()
    {
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(
            handler: null,
            answerAll: true,
            customResponder: (method, _, _) => method == "slow"
                ? firstGate.Task.ContinueWith(_ => (object?)new { ok = true, name = "slow" }, CancellationToken.None)
                : Task.FromResult<object?>(new { ok = true, name = "fast" }));
        try
        {
            var slowTask = harness.Connection.RequestAsync("slow", null, CancellationToken.None);
            await WaitUntilAsync(() => harness.Server.Received.Any(r => r.MethodOrCommand == "slow"));

            var fastResult = await harness.Connection.RequestAsync("fast", null, CancellationToken.None);
            Assert.Equal("fast", fastResult.GetProperty("name").GetString());

            firstGate.TrySetResult();
            var slowResult = await slowTask.WaitAsync(Timeout);
            Assert.Equal("slow", slowResult.GetProperty("name").GetString());
        }
        finally
        {
            await harness.CleanupAsync();
        }
    }

    [Fact]
    public async Task NotificationsReachTheInboundHandlerAndNeverResolvePending()
    {
        var notificationSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = CreateHarness(
            handler: (message, _) =>
            {
                if (message.IsNotification && message.Method == "window/logMessage")
                {
                    notificationSeen.TrySetResult(message.Params?.GetProperty("message").GetString() ?? string.Empty);
                }

                return Task.FromResult<object?>(null);
            },
            answerAll: false);
        try
        {
            await harness.Server.SendRawAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\",\"params\":{\"type\":1,\"message\":\"hello from server\"}}");

            Assert.Equal("hello from server", await notificationSeen.Task.WaitAsync(Timeout));
            Assert.Empty(harness.Server.Received); // notifications are inbound to the client; the scripted peer never receives them
        }
        finally
        {
            await harness.CleanupAsync();
        }
    }

    [Fact]
    public async Task PumpCloseFaultsEveryPendingRequestWithIOException()
    {
        var harness = CreateHarness(handler: null, answerAll: false);
        try
        {
            var requestTask = harness.Connection.RequestAsync("never-answered", null, CancellationToken.None);
            await WaitUntilAsync(() => harness.Server.Received.Count == 1);

            harness.Process.SimulateExit();

            var exception = await Assert.ThrowsAsync<IOException>(() => requestTask.WaitAsync(Timeout));
            Assert.Contains("closed", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await harness.CleanupAsync();
        }
    }

    [Fact]
    public async Task InboundRequestGetsCannedResponseFromHandler()
    {
        var harness = CreateHarness(
            handler: (message, _) => message.Method == "workspace/configuration"
                ? Task.FromResult<object?>(new { items = new object[] { new { } } })
                : Task.FromResult<object?>(null),
            answerAll: false);
        try
        {
            await harness.Server.SendRawAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"workspace/configuration\",\"params\":{\"items\":[]}}");

            await WaitUntilAsync(() => harness.Server.ReceivedResponses.Any(r => r.Id == 99));
            var (id, success, body) = Assert.Single(harness.Server.ReceivedResponses);
            Assert.Equal(99, id);
            Assert.True(success);
            Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        }
        finally
        {
            await harness.CleanupAsync();
        }
    }

    private static Harness CreateHarness(
        Func<InboundRpcMessage, CancellationToken, Task<object?>>? handler,
        bool answerAll,
        Func<string, JsonElement, int, Task<object?>>? customResponder = null)
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake-lsp"));
        var server = new ScriptedWireServer(process, WireProtocol.LspJsonRpc)
        {
            OnRequest = customResponder
                ?? (answerAll
                    ? (_, parameters, _) => Task.FromResult<object?>(new { ok = true, echoed = parameters })
                    : null),
        };
        server.Start();

        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pumpTask = connection.PumpAsync(handler ?? ((_, _) => Task.FromResult<object?>(null)), CancellationToken.None);
        return new Harness(process, server, connection, pumpTask);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness(FakeServerProcess process, ScriptedWireServer server, FramedJsonRpcConnection connection, Task pumpTask)
    {
        public FakeServerProcess Process { get; } = process;

        public ScriptedWireServer Server { get; } = server;
        public FramedJsonRpcConnection Connection { get; } = connection;


        public async Task CleanupAsync()
        {
            await Connection.DisposeAsync();
            process.Dispose(); // completes the fake pipes → pump gets EOF and exits
            try { await pumpTask.WaitAsync(Timeout); }
            catch (Exception) { /* pump exits with IO/OCE on close — expected */ }
            await Server.DisposeAsync();
        }
    }
}
