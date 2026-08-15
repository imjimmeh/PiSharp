using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// End-to-end integration of a live <see cref="PiServerHost"/> against a real WebSocket client.
/// The host runs in-process on an ephemeral port; the client is a bare <see cref="ClientWebSocket"/>
/// speaking the server JSON protocol directly. (PiSharp.Server.Tests does not reference PiSharp.Client,
/// so the <c>ClientWebSocketTransport</c> abstraction is unavailable here — a raw socket is the
/// faithful equivalent and exercises the same wire protocol.)
///
/// No LLM/provider is reachable from these tests: <see cref="PiServerHost"/> builds its own
/// <c>ServerSessionRegistry</c> with the real bootstrap runtime factory, and the <c>prompt</c> command
/// would require a provider. Instead of a real prompt, the <c>run_command</c> host delegate emits
/// server-side events into the retained event log via <see cref="LiveServerSession.EmitEvent"/>,
/// which is the event source these integration scenarios exercise (attach/replay/stream/abort/fork).
/// </summary>
public sealed class HostIntegrationTests
{
    private const string ApiKey = "itest-key";

    [Fact]
    public async Task CreateSession_Attach_Replay_Abort_RoundTripsOverWebSocket()
    {
        var root = NewTempDir();
        await using var host = await StartHostAsync((session, text) =>
        {
            session.EmitEvent(AgentSessionEvent.FromServer("system_message", new { text }));
            return new ServerCommandResult(true, text);
        });

        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var create = await client.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);
        var sessionId = create.RootElement.GetProperty("data").GetProperty("serverSessionId").GetString()!;
        Assert.StartsWith("srv_", sessionId);

        // Attach starts the per-socket event pump at the retained log head.
        var attach = await client.SendCommandAsync(new
        {
            id = "a", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = 0L,
        });
        AssertSuccess(attach);

        // run_command emits a server event daemon-side; the event pump streams it back over the socket.
        var run = await client.SendCommandAsync(new
        {
            id = "r", type = ServerCommandTypes.RunCommand, serverSessionId = sessionId, text = "stream-1",
        });
        AssertSuccess(run);

        var ev = await client.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "stream-1");
        Assert.True(ev.Sequence > 0);

        var abort = await client.SendCommandAsync(new
        {
            id = "ab", type = ServerCommandTypes.Abort, serverSessionId = sessionId,
        });
        AssertSuccess(abort);
    }

    [Fact]
    public async Task TwoClients_LateJoin_GetsReplayFromCursor()
    {
        var root = NewTempDir();
        await using var host = await StartHostAsync((session, text) =>
        {
            session.EmitEvent(AgentSessionEvent.FromServer("system_message", new { text }));
            return new ServerCommandResult(true, text);
        });

        // First client creates the session, attaches, and produces a retained event.
        await using var clientA = new RawClient();
        await clientA.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);
        var create = await clientA.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);
        var sessionId = create.RootElement.GetProperty("data").GetProperty("serverSessionId").GetString()!;

        await clientA.SendCommandAsync(new { id = "a", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = 0L });
        await clientA.SendCommandAsync(new { id = "r1", type = ServerCommandTypes.RunCommand, serverSessionId = sessionId, text = "first" });
        await clientA.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "first");
        var aWatermark = clientA.LastEventSequence;

        // Second client attaches late from cursor 0 and must receive the retained event via replay.
        await using var clientB = new RawClient();
        await clientB.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);
        await clientB.SendCommandAsync(new { id = "b", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = aWatermark });
        var replayed = await clientB.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "first");
        Assert.Equal(aWatermark, replayed.Sequence);
    }

    [Fact]
    public async Task Fork_ChangesRuntimeSession_ReconnectReplaysRetainedEvents()
    {
        var root = NewTempDir();
        await using var host = await StartHostAsync((session, text) =>
        {
            session.EmitEvent(AgentSessionEvent.FromServer("system_message", new { text }));
            return new ServerCommandResult(true, text);
        });

        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);
        var create = await client.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);
        var createData = create.RootElement.GetProperty("data");
        var sessionId = createData.GetProperty("serverSessionId").GetString()!;
        var runtimeIdBefore = createData.GetProperty("state").GetProperty("runtimeSessionId").GetString()!;

        await client.SendCommandAsync(new { id = "a", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = 0L });
        await client.SendCommandAsync(new { id = "r1", type = ServerCommandTypes.RunCommand, serverSessionId = sessionId, text = "before-fork" });
        var before = await client.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "before-fork");

        // Fork rebinds the live session to a new runtime session id while the server session id is stable.
        var fork = await client.SendCommandAsync(new
        {
            id = "f", type = ServerCommandTypes.Fork, serverSessionId = sessionId,
            entryId = (string?)null, newSessionId = (string?)null,
        });
        AssertSuccess(fork);
        var forkData = fork.RootElement.GetProperty("data");
        Assert.Equal(sessionId, forkData.GetProperty("serverSessionId").GetString());
        var runtimeIdAfter = forkData.GetProperty("runtimeSessionId").GetString()!;
        Assert.NotEqual(runtimeIdBefore, runtimeIdAfter);

        // Events keep flowing on the same server session id after the fork rebind.
        await client.SendCommandAsync(new { id = "r2", type = ServerCommandTypes.RunCommand, serverSessionId = sessionId, text = "after-fork" });
        var after = await client.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "after-fork");
        var watermark = after.Sequence;
        var replayFrom = before.Sequence;

        // Disconnect, reconnect, and replay from the first retained event: both retained events are redelivered in order.
        await client.DisposeAsync();
        await using var client2 = new RawClient();
        await client2.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);
        await client2.SendCommandAsync(new { id = "a2", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = replayFrom });
        var first = await client2.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "before-fork");
        var second = await client2.WaitForEventAsync(e => e.Type == "system_message" && e.Text == "after-fork");
        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(watermark, second.Sequence);
    }

    [Fact]
    public async Task Shutdown_ReturnsSuccessOverWebSocket()
    {
        await using var host = await StartHostAsync((session, text) => new ServerCommandResult(true, text));

        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        // Shutdown is session-independent: the CLI daemon-stop sends a bare envelope
        // (ShutdownRequest.ConfirmationToken is optional), so no payload is required.
        var response = await client.SendCommandAsync(
            new ServerCommandEnvelope(ServerCommandTypes.Shutdown, Id: "shutdown"));

        AssertSuccess(response);
        Assert.Equal(ServerCommandTypes.Shutdown, response.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task CreateSession_ForwardsBootstrapDiagnosticsToHostLoggerFactory()
    {
        var root = NewTempDir();
        var messages = new ConcurrentQueue<string>();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Debug).AddProvider(new CapturingLoggerProvider(messages)));
        await using var host = await StartHostAsync(
            (session, text) => new ServerCommandResult(true, text),
            loggerFactory);

        await using var client = new RawClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        using var create = await client.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);

        Assert.Contains(messages, message => message.Contains("bootstrap: create-session start", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("bootstrap: create-session complete", StringComparison.Ordinal));
    }

    // --- shared helpers ---

    private static async Task<PiServerHost> StartHostAsync(
        Func<LiveServerSession, string, ServerCommandResult> emitOnRunCommand,
        ILoggerFactory? loggerFactory = null)
    {
        var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            // Keep the idle sweep far away so sessions survive disconnects between reconnects.
            IdleTimeout = TimeSpan.FromHours(1),
            LoggerFactory = loggerFactory,
            RunCommandAsync = (context, text, options, ct) =>
            {
                var result = emitOnRunCommand(context.Session, text);
                return Task.FromResult(result);
            },
        });
        await host.StartAsync(0);
        return host;
    }

    private static Uri HostUri(PiServerHost host) => new($"ws://127.0.0.1:{host.Port}/ws");

    /// <summary>A <c>create_session</c> frame whose runtime is stripped down (no tools, no resources)
    /// so the bootstrap never reaches for an LLM provider or on-disk extensions.</summary>
    private static object CreateFrame(string root) => new
    {
        id = "create",
        type = ServerCommandTypes.CreateSession,
        cwd = root,
        sessionsRoot = Path.Combine(root, "sessions"),
        noTools = true,
        noBuiltinTools = true,
        noExtensions = true,
        noSkills = true,
        noPromptTemplates = true,
        noThemes = true,
        noContextFiles = true,
    };

    private static void AssertSuccess(JsonDocument response)
    {
        Assert.True(
            response.RootElement.GetProperty("success").GetBoolean(),
            "command failed: " + (response.RootElement.TryGetProperty("error", out var err)
                ? err.GetProperty("message").GetString()
                : response.RootElement.GetRawText()));
    }

    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-host-itest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record EventFrame(long Sequence, string Type, string? Text, string ServerSessionId);

    /// <summary>
    /// Minimal real WebSocket client: sends JSON command frames and correlates responses by id while
    /// collecting event frames in arrival order. Mirrors the read loop of ClientWebSocketTransport
    /// without depending on PiSharp.Client.
    /// </summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) messages.Enqueue($"{categoryName}: {formatter(state, exception)}");
        }
    }
    private sealed class RawClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
        private readonly ConcurrentQueue<EventFrame> _events = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _readTask;
        private long _lastEventSequence;
        private int _disposed;

        public long LastEventSequence => Interlocked.Read(ref _lastEventSequence);

        public async Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
        {
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            await _socket.ConnectAsync(uri, ct);
            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
        }

        public async Task<JsonDocument> SendCommandAsync(object frame, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(frame, ServerJsonSerializer.Options);
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Command frame requires an 'id'.");
            var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    return await tcs.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No response for command id '{id}'.");
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public async Task<EventFrame> WaitForEventAsync(Func<EventFrame, bool> predicate, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                foreach (var ev in _events)
                {
                    if (predicate(ev)) return ev;
                }
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Event not received within timeout.");
                await Task.Delay(15);
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    string? json;
                    using (var stream = new MemoryStream())
                    {
                        ValueWebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync((Memory<byte>)buffer, ct);
                            if (result.MessageType == WebSocketMessageType.Close) return;
                            stream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);
                        json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    }

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(json); }
                    catch (JsonException) { continue; }

                    var type = doc.RootElement.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                    switch (type)
                    {
                        case "response":
                            var id = doc.RootElement.GetProperty("id").GetString();
                            if (id is not null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(doc);
                            break;
                        case "event":
                            AppendEvent(doc.RootElement);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        private void AppendEvent(JsonElement root)
        {
            var seq = root.GetProperty("sequence").GetInt64();
            var sessionId = root.GetProperty("serverSessionId").GetString() ?? string.Empty;
            var evt = root.GetProperty("event");
            var eventType = evt.TryGetProperty("type", out var et) ? et.GetString() ?? "unknown" : "unknown";
            var text = evt.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            _events.Enqueue(new EventFrame(seq, eventType, text, sessionId));
            Interlocked.Exchange(ref _lastEventSequence, seq);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            try { await (_readTask ?? Task.CompletedTask); } catch { }
            try { _socket.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
