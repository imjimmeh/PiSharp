using System.Net;
using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Mcp.Tests;

/// <summary>
/// In-process MCP HTTP server mock: RPC requests answered inline as JSON (streamable HTTP), and a
/// legacy-SSE event stream that announces the message endpoint and relays responses as
/// <c>message</c> events; notifications answered with 202 + empty body.
/// </summary>
internal sealed class MockHttpMcpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serveTask;
    private readonly Channel<string> _sseMessages = Channel.CreateUnbounded<string>();
    public readonly ConcurrentQueue<string> Authorizations = new();

    public MockHttpMcpServer()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{McpOAuthRedirect.PickFreePort()}/");
        _listener.Start();
        BaseUrl = _listener.Prefixes.First();
        _serveTask = Task.Run(ServeLoopAsync);
    }

    public string BaseUrl { get; }

    private async Task ServeLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { break; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var authorization = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(authorization)) Authorizations.Enqueue(authorization);

        if (request.HttpMethod == "GET")
        {
            // Legacy-SSE event stream: announce the message endpoint, then relay JSON-RPC
            // responses as `message` events until shutdown.
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            var stream = context.Response.OutputStream;
            var hello = Encoding.UTF8.GetBytes($"event: endpoint\ndata: {BaseUrl}mcp\n\n");
            await stream.WriteAsync(hello, _cts.Token);
            await stream.FlushAsync(_cts.Token);
            try
            {
                await foreach (var message in _sseMessages.Reader.ReadAllAsync(_cts.Token))
                {
                    var bytes = Encoding.UTF8.GetBytes($"event: message\ndata: {message}\n\n");
                    await stream.WriteAsync(bytes, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        var json = JsonNode.Parse(body)?.AsObject();
        if (json is null)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        // Notifications (no id) are acknowledged with 202 and no JSON body.
        if (!json.TryGetPropertyValue("id", out _))
        {
            context.Response.StatusCode = 202;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return;
        }

        var method = json["method"]?.GetValue<string>();
        JsonNode result = method switch
        {
            "server/discover" => new JsonObject
            {
                ["supportedVersions"] = new JsonArray("2025-11-25"),
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "mock-http", ["version"] = "1.0.0" }
            },
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "mock-http", ["version"] = "1.0.0" }
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "echo",
                    ["description"] = "Echo text",
                    ["inputSchema"] = new JsonObject { ["type"] = "object" }
                })
            },
            "tools/call" => new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "pong" })
            },
            _ => throw new InvalidOperationException($"Unexpected method '{method}'.")
        };

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = json["id"]?.DeepClone(), ["result"] = result };
        var responseJson = response.ToJsonString();
        // Relay the response over the SSE stream as well, so legacy-SSE mode (which reads
        // replies from `message` events, not the POST body) sees it.
        await _sseMessages.Writer.WriteAsync(responseJson, _cts.Token);
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, _cts.Token);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _serveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        _listener.Close();
        _cts.Dispose();
    }
}

public sealed class HttpTransportFactoryTests
{
    [Fact]
    public async Task StreamableHttp_ConnectsListsAndCalls()
    {
        await using var server = new MockHttpMcpServer();
        var factory = new PiSharp.Mcp.Transports.Http.HttpTransportFactory();
        var config = TestMcp.HttpServer(server.BaseUrl + "mcp", "streamable-http");

        var transport = await factory.CreateAsync(config, TestMcp.Context(), CancellationToken.None);
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: null);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var echo = Assert.Single(tools);
        Assert.Equal("echo", echo.Name);

        var result = await echo.CallAsync(TestMcp.JsonArgs(("text", "hi")), progress: null, options: null, cancellationToken: CancellationToken.None);
        Assert.Equal("pong", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task SseMode_ConnectsAndLists()
    {
        await using var server = new MockHttpMcpServer();
        var factory = new PiSharp.Mcp.Transports.Http.HttpTransportFactory();
        var config = TestMcp.HttpServer(server.BaseUrl + "mcp", "sse");

        var transport = await factory.CreateAsync(config, TestMcp.Context(), CancellationToken.None);
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: null);

        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.Single(tools);
    }

    [Fact]
    public async Task LiteralToken_IsSentAsBearerHeader()
    {
        await using var server = new MockHttpMcpServer();
        var factory = new PiSharp.Mcp.Transports.Http.HttpTransportFactory();
        var config = TestMcp.HttpServer(server.BaseUrl + "mcp", "streamable-http") with
        {
            Auth = new McpAuthConfig(McpAuthKind.Literal, LiteralToken: "sekret")
        };

        var transport = await factory.CreateAsync(config, TestMcp.Context(), CancellationToken.None);
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: null);
        await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(server.Authorizations, header => header == "Bearer sekret");
    }

    [Fact]
    public async Task EnvToken_ResolvesFromEnvironment()
    {
        await using var server = new MockHttpMcpServer();
        var factory = new PiSharp.Mcp.Transports.Http.HttpTransportFactory();
        var config = TestMcp.HttpServer(server.BaseUrl + "mcp", "streamable-http") with
        {
            Auth = new McpAuthConfig(McpAuthKind.Env, EnvVar: "MCP_TEST_TOKEN")
        };
        Environment.SetEnvironmentVariable("MCP_TEST_TOKEN", "from-env");

        try
        {
            var transport = await factory.CreateAsync(config, TestMcp.Context(), CancellationToken.None);
            await using var client = await McpClient.CreateAsync(transport, loggerFactory: null);
            await client.ListToolsAsync(cancellationToken: CancellationToken.None);
            Assert.Contains(server.Authorizations, header => header == "Bearer from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_TEST_TOKEN", null);
        }
    }

    [Fact]
    public async Task StoredOAuthToken_IsSentAsBearerHeader()
    {
        await using var server = new MockHttpMcpServer();
        var storage = new InMemoryOAuthStorage();
        await storage.SetOAuthCredentialsAsync("mcp:weather",
            new OAuthCredentials(Refresh: "refresh-1", Access: "access-1", Expires: DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()),
            CancellationToken.None);
        var factory = new PiSharp.Mcp.Transports.Http.HttpTransportFactory();
        var config = TestMcp.HttpServer(server.BaseUrl + "mcp", "streamable-http") with
        {
            Auth = new McpAuthConfig(McpAuthKind.OAuth)
        };

        var transport = await factory.CreateAsync(config, TestMcp.Context(storage), CancellationToken.None);
        await using var client = await McpClient.CreateAsync(transport, loggerFactory: null);
        await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(server.Authorizations, header => header == "Bearer access-1");
    }
}
