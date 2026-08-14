using System.Text.Json;
using PiSharp.Runtime;
using Xunit;

namespace PiSharp.Acp.Tests;

/// <summary>
/// End-to-end JSON-RPC framing, dispatch, notification fan-out and turn lifecycle over an
/// in-memory stdio duplex (plan §3.7 / §9 / §10). Uses the duplex so dynamic values (the
/// <c>sessionId</c> from <c>session/new</c>) can be injected and interactive turns can be driven
/// deterministically.
/// </summary>
public sealed class AcpServerTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":"abc","method":"initialize","params":{}}""";

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndEchoesStringId()
    {
        await using var sc = await Start();
        await sc.SendAsync(Initialize);
        var root = J(await sc.ReceiveAsync()).RootElement;
        Assert.Equal("abc", root.GetProperty("id").GetString());
        Assert.Equal(1, root.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        Assert.Equal("pisharp", root.GetProperty("result").GetProperty("agentInfo").GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        await using var sc = await Start();
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        await sc.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"bogus/method"}""");
        var root = J(await sc.ReceiveAsync()).RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal(AcpErrorCodes.MethodNotFound, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task MalformedJson_ReturnsParseError()
    {
        await using var sc = await Start();
        await sc.SendAsync("{ this is not json");
        var root = J(await sc.ReceiveAsync()).RootElement;
        Assert.Equal(AcpErrorCodes.ParseError, root.GetProperty("error").GetProperty("code").GetInt32());
        // The parse-error response carries no recoverable request id (the writer omits null ids).
        Assert.True(!root.TryGetProperty("id", out _) || root.GetProperty("id").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task SessionNew_WrongCwd_ReturnsInvalidParams()
    {
        await using var sc = await Start();
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        await sc.SendAsync(Req(0, "session/new", new { cwd = "C:/not/the/process/cwd" }));
        var root = J(await sc.ReceiveAsync()).RootElement;
        Assert.Equal(AcpErrorCodes.InvalidParams, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SessionPrompt_NoActiveSession_ReturnsServerError()
    {
        await using var sc = await Start();
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        await sc.SendAsync(Req(0, "session/prompt", new { sessionId = "sess_x", prompt = TextPrompt("hi") }));
        var root = J(await ReadUntilContainingAsync(sc, "error")).RootElement;
        Assert.Equal(AcpErrorCodes.ServerError, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SessionNew_ThenPrompt_ReturnsEndTurn()
    {
        var runtime = await AcpTestRuntime.CreateAsync(stream: AcpTestRuntime.FakeStream("hi there"));
        var cwd = runtime.Session.Metadata.Cwd;
        await using var sc = await Start(runtime);
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        var sessionId = await NewSessionAsync(sc, cwd);

        await sc.SendAsync(Req(1, "session/prompt", new { sessionId, prompt = TextPrompt("hello") }));
        var root = J(await ReadUntilStopReasonAsync(sc)).RootElement;
        Assert.Equal("end_turn", root.GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task SessionCancel_Notification_IsNeverAnswered()
    {
        await using var sc = await Start();
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        await sc.SendAsync("""{"jsonrpc":"2.0","method":"session/cancel","params":{}}""");
        Assert.Null(await sc.ReceiveAsync(timeoutMs: 300));
    }

    [Fact]
    public async Task CancelMidTurn_ReturnsCancelledStopReason()
    {
        var runtime = await AcpTestRuntime.CreateAsync(stream: AcpTestRuntime.HangingStartStream);
        var cwd = runtime.Session.Metadata.Cwd;
        await using var sc = await Start(runtime);
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        var sessionId = await NewSessionAsync(sc, cwd);

        await sc.SendAsync(Req(1, "session/prompt", new { sessionId, prompt = TextPrompt("go") }));
        // Wait until the turn has started streaming (agent_message_chunk from the start text).
        await ReadUntilContainingAsync(sc, "agent_message_chunk");
        await sc.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } }));
        var root = J(await ReadUntilStopReasonAsync(sc)).RootElement;
        Assert.Equal("cancelled", root.GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task TurnInProgress_SecondPromptWhileTurnActive_ReturnsServerError()
    {
        var runtime = await AcpTestRuntime.CreateAsync(stream: AcpTestRuntime.HangingStartStream);
        var cwd = runtime.Session.Metadata.Cwd;
        await using var sc = await Start(runtime);
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        var sessionId = await NewSessionAsync(sc, cwd);

        await sc.SendAsync(Req(1, "session/prompt", new { sessionId, prompt = TextPrompt("first") }));
        await ReadUntilContainingAsync(sc, "agent_message_chunk"); // turn now active

        await sc.SendAsync(Req(2, "session/prompt", new { sessionId, prompt = TextPrompt("second") }));
        var root = J(await ReadUntilErrorAsync(sc)).RootElement;
        Assert.Equal(AcpErrorCodes.ServerError, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("turn_in_progress", root.GetProperty("error").GetProperty("message").GetString());

        // clean up the dangling turn
        await sc.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } }));
        await ReadUntilStopReasonAsync(sc);
    }

    [Fact]
    public async Task SessionLoad_WithReplay_EmitsSessionUpdates()
    {
        var runtime = await AcpTestRuntime.CreateAsync(stream: AcpTestRuntime.FakeStream("persisted text"));
        var cwd = runtime.Session.Metadata.Cwd;
        var persisting = new AcpSessionManager(runtime);
        var info = await persisting.NewAsync(cwd);
        await persisting.PromptAsync(info.SessionId, [new AcpTextBlock("hello")]);
        await persisting.CloseAsync(info.SessionId);

        // Load it back with replay on a fresh server.
        var fresh = new AcpSessionManager(runtime);
        await using var sc = await Start(runtime, fresh);
        await sc.SendAsync(Initialize); await sc.ReceiveAsync();
        await sc.SendAsync(Req(3, "session/load", new { sessionId = info.SessionId, cwd, replay = true }));

        // Replay emits session/update notifications before the (null-result) response.
        Assert.NotNull(await ReadUntilContainingAsync(sc, "session/update"));
        Assert.NotNull(await ReadUntilContainingAsync(sc, "\"id\":3"));
    }

    // --- helpers ---

    private static readonly AcpModeOptions Yolo = new(AcpApprovalMode.Yolo);

    private static string Req(long id, string method, object? p) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = p }, options: null);

    private static object[] TextPrompt(string text) => [new { type = "text", text }];

    private static JsonDocument J(string? line) => JsonDocument.Parse(line ?? throw new InvalidOperationException("unexpected EOF"));

    private static async Task<string?> ReadUntilContainingAsync(ServerContext sc, string token)
    {
        while (true)
        {
            var line = await sc.ReceiveAsync();
            if (line is null) return null;
            if (line.Contains(token)) return line;
        }
    }
    private static Task<string?> ReadUntilStopReasonAsync(ServerContext sc) => ReadUntilContainingAsync(sc, "stopReason");
    private static Task<string?> ReadUntilErrorAsync(ServerContext sc) => ReadUntilContainingAsync(sc, "\"error\"");

    private static async Task<string> NewSessionAsync(ServerContext sc, string cwd)
    {
        await sc.SendAsync(Req(0, "session/new", new { cwd }));
        var root = J(await sc.ReceiveAsync()).RootElement;
        return root.GetProperty("result").GetProperty("sessionId").GetString()!;
    }

    private static async Task<ServerContext> Start(SessionRuntime? runtime = null, AcpSessionManager? manager = null)
    {
        var runtime2 = runtime ?? await AcpTestRuntime.CreateAsync();
        var mgr = manager ?? new AcpSessionManager(runtime2);
        var server = new AcpServer(new AcpServerOptions(mgr, Yolo, [], null));
        return new ServerContext(server);
    }

    /// <summary>Wraps a running server over a duplex; completes input on dispose so RunAsync exits.</summary>
    private sealed class ServerContext : IAsyncDisposable
    {
        private readonly AcpTestDuplex _duplex = new();
        private readonly Task _runTask;

        public ServerContext(AcpServer server)
        {
            _runTask = server.RunAsync(_duplex.Input, _duplex.Output, CancellationToken.None);
        }

        public Task SendAsync(string line) => _duplex.SendAsync(line);

        public async Task<string?> ReceiveAsync(int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try { return await _duplex.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { return null; }
        }

        public async ValueTask DisposeAsync()
        {
            _duplex.CompleteInput();
            try { await _runTask; }
            finally { _duplex.Dispose(); }
        }
    }
}
