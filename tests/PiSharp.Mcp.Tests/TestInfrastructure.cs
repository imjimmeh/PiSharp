using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PiSharp.Abstractions.Messages;
using PiSharp.Ai.Registry;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Auth;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;

namespace PiSharp.Mcp.Tests;

/// <summary>One direction of an in-memory pipe. EOF after <see cref="Complete"/>; writes after
/// completion are swallowed. Safe for both ends of an SDK stream transport.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly object _gate = new();
    private readonly MemoryStream _buffer = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _complete;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }

    public void Complete()
    {
        lock (_gate) _complete = true;
        _signal.Release();
    }

    protected override void Dispose(bool disposing)
    {
        Complete();
        base.Dispose(disposing);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_buffer.Length > 0)
                {
                    var read = _buffer.Read(buffer, offset, count);
                    if (_buffer.Length == 0) _buffer.SetLength(0);
                    return read;
                }
                if (_complete) return 0;
            }
            await _signal.WaitAsync(cancellationToken);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_buffer.Length > 0)
                {
                    var read = _buffer.Read(buffer.Span);
                    if (_buffer.Length == 0) _buffer.SetLength(0);
                    return read;
                }
                if (_complete) return 0;
            }
            await _signal.WaitAsync(cancellationToken);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate) _buffer.Write(buffer, offset, count);
        _signal.Release();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        lock (_gate) _buffer.Write(buffer, offset, count);
        _signal.Release();
        return Task.CompletedTask;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        lock (_gate) _buffer.Write(buffer.Span);
        _signal.Release();
        await Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

public static class TestMessages
{
    public static string UserText(CapturedMessage message)
        => message.Message is UserMessage user && user.Content.FirstOrDefault() is TextContent text
            ? text.Text
            : string.Empty;
}


/// <summary>In-process MCP server speaking the protocol over two <see cref="DuplexStream"/>s.</summary>
internal sealed class MockMcpServer : IAsyncDisposable
{
    private readonly DuplexStream _clientToServer;
    private readonly DuplexStream _serverToClient;
    private readonly McpServer _server;
    private readonly Task _runTask;

    public MockMcpServer(
        IReadOnlyList<Tool> tools,
        Func<CallToolRequestParams, CancellationToken, Task<CallToolResult>>? callHandler = null)
    {
        _clientToServer = new DuplexStream();
        _serverToClient = new DuplexStream();

        var handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools.ToList() }),
            CallToolHandler = async (context, cancellationToken) =>
                callHandler is null
                    ? new CallToolResult { Content = [new TextContentBlock { Text = "mock ok" }] }
                    : await callHandler(context.Params, cancellationToken)
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "mock-server", Version = "1.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = handlers
        };

        _server = McpServer.Create(
            new StreamServerTransport(_clientToServer, _serverToClient, "mock", loggerFactory: null),
            options,
            loggerFactory: null,
            serviceProvider: null);
        _runTask = _server.RunAsync(CancellationToken.None);
    }

    public IClientTransport CreateClientTransport()
        => new StreamClientTransport(_serverToClient, _clientToServer, loggerFactory: null);

    public async ValueTask DisposeAsync()
    {
        _clientToServer.Complete();
        _serverToClient.Complete();
        try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        await _server.DisposeAsync();
    }
}

/// <summary>Returns the mock's in-process client transport without touching the static registry.</summary>
internal sealed class TestStreamTransportFactory(MockMcpServer mock) : IMcpTransportFactory
{
    public string Kind => "stdio";
    public bool CanCreate(McpServerConfig config) => config.Transport == McpTransportKind.Stdio;
    public ValueTask<IClientTransport> CreateAsync(McpServerConfig config, McpTransportContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(mock.CreateClientTransport());
}

/// <summary>Real-registry <see cref="IExtensionApi"/> for host/session tests.</summary>
internal sealed class TestExtensionApi : IExtensionApi
{
    private readonly List<CapturedMessage> _sent = [];

    public TestExtensionApi()
    {
        Registry = new ExtensionRegistry();
        Events = new ExtensionEventBus(Registry, Descriptor.EffectiveSourceId);
        Settings = new InMemorySettingsApi();
    }

    public ExtensionRegistry Registry { get; }
    public string Cwd { get; set; } = Path.GetTempPath();
    public bool HasUi { get; set; }
    public IExtensionUi Ui { get; set; } = NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; set; } = new("pisharp-mcp", "MCP Client", "0.1.0");
    public IExtensionEventBus Events { get; }
    public IExtensionSettingsApi Settings { get; }
    public IReadOnlyList<CapturedMessage> SentMessages => _sent;

    public IDisposable On(string eventName, ExtensionEventHandler handler) => Registry.RegisterHandler(Descriptor.EffectiveSourceId, eventName, handler);
    public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException("TestExtensionApi.Use is not supported.");
    public IDisposable RegisterTool(ExtensionToolRegistration registration) => Registry.RegisterTool(Descriptor.EffectiveSourceId, registration.ToAgentTool(), registration.Override);
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException("TestExtensionApi.RegisterSkill is not supported.");
    public IDisposable RegisterCommand(ExtensionCommandRegistration registration) => Registry.RegisterCommand(Descriptor.EffectiveSourceId, registration);
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException("TestExtensionApi.RegisterShortcut is not supported.");
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => throw new NotSupportedException("TestExtensionApi.RegisterFlag is not supported.");
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException("TestExtensionApi.RegisterMessageRenderer is not supported.");
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException("TestExtensionApi.RegisterMessageDecorator is not supported.");
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException("TestExtensionApi.RegisterProvider is not supported.");
    public bool RemoveProvider(string api) => throw new NotSupportedException("TestExtensionApi.RemoveProvider is not supported.");
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();

    public IExtensionSessionApi Session => throw new NotSupportedException("TestExtensionApi.Session is not supported.");
    public IExtensionToolApi Tools => throw new NotSupportedException("TestExtensionApi.Tools is not supported.");
    public IExtensionSkillApi Skills => throw new NotSupportedException("TestExtensionApi.Skills is not supported.");
    public IExtensionModelApi Model => throw new NotSupportedException("TestExtensionApi.Model is not supported.");
    public IExtensionPromptApi Prompt => throw new NotSupportedException("TestExtensionApi.Prompt is not supported.");
    public IExtensionStateApi State => throw new NotSupportedException("TestExtensionApi.State is not supported.");

    public Task SendMessageAsync(
        AgentMessage message,
        ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp,
        bool triggerTurn = false,
        CancellationToken cancellationToken = default)
    {
        _sent.Add(new CapturedMessage(message, delivery, triggerTurn, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}

/// <summary>Settings store keyed by raw (already-namespaced) keys with change notification.</summary>
internal sealed class InMemorySettingsApi : IExtensionSettingsApi
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<Action<ExtensionSettingsChange>> _handlers = [];

    public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public T? Get<T>(string key)
    {
        if (!_values.TryGetValue(key, out var value) || value is null) return default;
        if (value is T typed) return typed;
        try { return (T)Convert.ChangeType(value, typeof(T)); }
        catch (InvalidCastException) { return default; }
        catch (FormatException) { return default; }
    }

    public object? GetCore(string path) => Get(path);

    public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        var change = new ExtensionSettingsChange(key, value, "source", "test");
        foreach (var handler in _handlers.ToArray()) handler(change);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
        => SetAsync(key, null, scope, cancellationToken);

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
    {
        _handlers.Add(handler);
        return new ChangeSubscription(() => _handlers.Remove(handler));
    }

    public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler)
        => OnChange(change =>
        {
            if (change.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) handler(change);
        });

    private sealed class ChangeSubscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

internal static class TestMcp
{
    public static McpTransportContext Context(IOAuthStorage? storage = null)
        => McpTransportContext.Create(storage ?? new InMemoryOAuthStorage());

    public static McpServerConfig StdioServer(string name = "fileserver", bool enabled = true)
        => new(
            Name: name,
            Source: "settings",
            Transport: McpTransportKind.Stdio,
            Command: "mock-server",
            Args: null,
            Env: null,
            Cwd: null,
            Url: null,
            HttpMode: null,
            Headers: null,
            Auth: null,
            Enabled: enabled);

    public static McpServerConfig HttpServer(string url = "http://127.0.0.1:1/mcp", string httpMode = "streamable-http", bool enabled = true)
        => new(
            Name: "weather",
            Source: "settings",
            Transport: McpTransportKind.Http,
            Command: null,
            Args: null,
            Env: null,
            Cwd: null,
            Url: url,
            HttpMode: httpMode,
            Headers: null,
            Auth: null,
            Enabled: enabled);

    public static Tool Tool(string name, string? description = null)
        => new()
        {
            Name = name,
            Description = description,
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""")
        };

    public static IReadOnlyDictionary<string, object?> JsonArgs(params (string Key, object Value)[] entries)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (key, value) in entries)
            dictionary[key] = value;
        return dictionary;
    }
    }
