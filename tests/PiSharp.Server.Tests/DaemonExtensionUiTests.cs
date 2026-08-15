using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PiSharp.Extensions;
using PiSharp.Permissions;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.UiBridge;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// Daemon-side extension UI forwarder (P01): every session an interactive client creates or
/// attaches to must have its extension binding's <see cref="IExtensionUi"/> replaced with a
/// <see cref="DaemonExtensionUi"/> that round-trips extension UI requests to the attached client
/// over the existing <c>ui_request</c>/<c>ui_response</c> lane. Sessions with no attached client
/// keep the safe hard deny with no round-trip latency and no orphan events.
/// </summary>
public sealed class DaemonExtensionUiTests
{
    private const string ApiKey = "itest-key";

    [Fact]
    public async Task CreateSession_BindsDaemonExtensionUi()
    {
        var root = NewTempDir();
        await using var host = await StartHostAsync();
        await using var client = new UiBridgeTestClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var create = await client.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);

        var live = Assert.Single(host.Registry.Sessions);
        var binding = live.Runtime.ExtensionBinding;
        Assert.IsType<DaemonExtensionUi>(binding.Ui);
        Assert.True(binding.HasUi);
    }

    [Fact]
    public async Task ExtensionUiRequest_RoundTripsToClientAndBack()
    {
        var root = NewTempDir();
        await using var host = await StartHostAsync();
        await using var client = new UiBridgeTestClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var create = await client.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);
        var sessionId = create.RootElement.GetProperty("data").GetProperty("serverSessionId").GetString()!;

        await client.SendCommandAsync(new { id = "a", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = 0L });
        var live = host.Registry.Sessions.Single(session => session.Id == sessionId);
        await WaitUntilAsync(() => live.AttachedClients > 0, TimeSpan.FromSeconds(8));

        // A permission request from the extension binding must surface as a ui_request the
        // attached client answers with "allow" (today: NoExtensionUi throws -> Ok == false).
        var payload = JsonSerializer.SerializeToElement(new { tool = "bash", reason = "run a command", defaultAnswer = "deny" });
        var resultTask = live.Runtime.ExtensionBinding.Ui.RequestAsync(
            new ExtensionUiRequest("ext-1", ApprovalClient.PermissionRequestKind, payload),
            CancellationToken.None);

        var intent = await client.WaitForUiRequestAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(ApprovalClient.PermissionRequestKind, intent.Kind);
        Assert.Equal("ext-1", intent.ExtensionId);
        Assert.Equal("bash", intent.Payload.GetProperty("tool").GetString());

        await client.SendCommandAsync(new
        {
            id = "ur",
            type = ServerCommandTypes.UiResponse,
            serverSessionId = sessionId,
            requestId = intent.RequestId,
            value = "allow",
            cancelled = false,
        });

        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(result.Ok, result.Error);
        Assert.Equal("allow", result.Value?.ToString());
    }

    [Fact]
    public async Task NoAttachedClient_DeniesImmediately_WithoutEmitting()
    {
        var root = NewTempDir();
        await using var host = await StartHostAsync();
        var client = new UiBridgeTestClient();
        await client.ConnectAsync(HostUri(host), ApiKey, CancellationToken.None);

        var create = await client.SendCommandAsync(CreateFrame(root));
        AssertSuccess(create);
        var sessionId = create.RootElement.GetProperty("data").GetProperty("serverSessionId").GetString()!;
        await client.SendCommandAsync(new { id = "a", type = ServerCommandTypes.Attach, serverSessionId = sessionId, sinceSequence = 0L });

        var live = host.Registry.Sessions.Single(session => session.Id == sessionId);
        // Drop the only attached client; the per-socket event pump cancels and detaches.
        await client.DisposeAsync();
        await WaitUntilAsync(() => live.AttachedClients == 0, TimeSpan.FromSeconds(8));

        var payload = JsonSerializer.SerializeToElement(new { tool = "bash", reason = "run a command", defaultAnswer = "deny" });
        var result = await live.Runtime.ExtensionBinding.Ui.RequestAsync(
            new ExtensionUiRequest("ext-1", ApprovalClient.PermissionRequestKind, payload),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("No interactive client is attached", result.Error);
        Assert.DoesNotContain(live.EventLog.ReplayFrom(0).Events, envelope => envelope.Event.Type == "ui_request");
    }

    // --- shared helpers (mirror HostIntegrationTests) ---

    private static async Task<PiServerHost> StartHostAsync()
    {
        var host = new PiServerHost(new PiServerHostOptions
        {
            ApiKey = ApiKey,
            // Keep the idle sweep far away so sessions survive disconnects between reconnects.
            IdleTimeout = TimeSpan.FromHours(1),
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
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ui-fwd-itest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(15);
        }
    }

    /// <summary>The <c>ui_request</c> event as observed on the wire: the payload properties are
    /// flattened into the event object next to the <c>type</c> discriminator (matching
    /// <c>AgentSessionEventJsonConverter.Write</c>), with null properties omitted.</summary>
    private sealed record WireUiIntent(string RequestId, string Kind, string? ExtensionId, JsonElement Payload)
    {
        public static WireUiIntent? From(JsonElement flatEvent)
        {
            if (flatEvent.ValueKind != JsonValueKind.Object || !flatEvent.TryGetProperty("requestId", out var requestId)) return null;
            return new WireUiIntent(
                requestId.GetString() ?? string.Empty,
                flatEvent.TryGetProperty("kind", out var kind) ? kind.GetString() ?? string.Empty : string.Empty,
                flatEvent.TryGetProperty("extensionId", out var extensionId) ? extensionId.GetString() : null,
                flatEvent.TryGetProperty("component", out var component) ? component.Clone() : default);
        }
    }

    /// <summary>
    /// Minimal real WebSocket client: sends JSON command frames, correlates responses by id, and
    /// answers <c>ui_request</c> events surfaced by the daemon. Mirrors the read loop of
    /// ClientWebSocketTransport without depending on PiSharp.Client.
    /// </summary>
    private sealed class UiBridgeTestClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
        private readonly ConcurrentQueue<WireUiIntent> _uiRequests = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _readTask;
        private readonly ConcurrentQueue<string> _eventTypes = new();

        private int _disposed;

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

        public async Task<WireUiIntent> WaitForUiRequestAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                foreach (var intent in _uiRequests)
                {
                    if (intent is not null) return intent;
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"ui_request event not received within timeout. Events seen: {string.Join(", ", _eventTypes)}");
                await Task.Delay(15);
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                var buffer = new byte[16 * 1024];
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
                            var evt = doc.RootElement.GetProperty("event");
                            if (evt.TryGetProperty("type", out var eventType))
                            {
                                _eventTypes.Enqueue(eventType.GetString() ?? string.Empty);
                                if (eventType.GetString() == "ui_request" && WireUiIntent.From(evt) is { } intent)
                                    _uiRequests.Enqueue(intent);
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test disposed", CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // socket already faulted or closed
            }
            try { await (_readTask ?? Task.CompletedTask); } catch { }
            try { _socket.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
