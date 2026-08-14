using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PiSharp.Extensions;

namespace PiSharp.Mcp;

/// <summary>
/// Owns one MCP server's lifecycle: transport creation via the registered factory, client
/// handshake, tool registration into the extension registry, unexpected-drop detection via
/// <c>McpClient.Completion</c>, and bounded exponential-backoff reconnection with tool-set
/// re-diff. Tool registrations use <see cref="ExtensionOverridePolicy.Reject"/> so a name
/// collision surfaces as a per-server error state, never a host crash.
/// </summary>
internal sealed class McpServerSession : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private readonly McpTransportContext _context;
    private readonly IExtensionApi _api;
    private readonly Func<McpTransportKind, IMcpTransportFactory?> _factoryResolver;
    private readonly McpHostOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IDisposable> _registeredTools = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    private McpClient? _client;
    private bool _disposed;
    private bool _stopping;
    private int _reconnectAttempt;
    private string _state = "disconnected";
    private string? _lastError;
    private string? _serverInfo;

    public McpServerSession(
        McpServerConfig config,
        McpTransportContext context,
        IExtensionApi api,
        McpHostOptions options,
        Func<McpTransportKind, IMcpTransportFactory?>? factoryResolver = null)
    {
        _config = config;
        _context = context;
        _api = api;
        _options = options;
        _factoryResolver = factoryResolver ?? McpServerRegistry.FindFactory;
    }

    public string Name => _config.Name;
    public string Source => _config.Source;
    public McpServerConfig Config => _config;

    public event Action<McpServerStatus>? StatusChanged;

    /// <summary>
    /// Connects (or reconnects) the server and registers its tools. Never throws for connection
    /// failures — they become the server's error state; cancellation leaves the server disconnected.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ConnectCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the session: unregisters tools and tears down the client and its transport.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _stopping = true;
            await DisposeClientCoreAsync();
            UnregisterTools();
            _reconnectAttempt = 0;
            _lastError = null;
            _serverInfo = null;
            SetState("disconnected");
        }
        finally
        {
            _gate.Release();
        }
    }

    public McpServerStatus GetStatus()
        => new(
            Name: _config.Name,
            Source: _config.Source,
            State: _state,
            ToolCount: _registeredTools.Count,
            LastError: _lastError,
            ServerInfo: _serverInfo,
            ReconnectAttempt: _state == "reconnecting" ? _reconnectAttempt : null);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        await DisconnectAsync();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await DisposeClientCoreAsync();
        UnregisterTools();
        _stopping = false;
        SetState("connecting");

        try
        {
            if (!_config.Validate(out var configError))
                throw new InvalidOperationException(configError);

            var factory = _factoryResolver(_config.Transport)
                ?? throw new InvalidOperationException($"No transport factory registered for kind '{_config.Transport}'.");
            var transport = await factory.CreateAsync(_config, _context, cancellationToken);

            var clientOptions = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = _api.Descriptor.Name, Version = _api.Descriptor.Version }
            };
            // A default (zero) ConnectTimeout must not be forwarded to the SDK, which treats
            // TimeSpan.Zero as an immediate initialization timeout.
            if (_options.ConnectTimeout > TimeSpan.Zero)
                clientOptions.InitializationTimeout = _options.ConnectTimeout;

            var client = await McpClient.CreateAsync(
                transport,
                clientOptions,
                loggerFactory: null,
                cancellationToken);
            _client = client;

            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var adapter = new McpToolAdapter(
                _config.Name,
                _options.ToolPrefix,
                _options.Truncation,
                (toolName, arguments, ct) => client.CallToolAsync(toolName, arguments, cancellationToken: ct).AsTask());

            RegisterTools(tools, adapter);

            _reconnectAttempt = 0;
            _lastError = null;
            _serverInfo = $"{client.ServerInfo.Name} {client.ServerInfo.Version}".Trim();
            SetState("connected");
            _ = ObserveCompletionAsync(client);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _lastError = null;
            await DisposeClientCoreAsync();
            UnregisterTools();
            SetState("disconnected");
            throw;
        }
        catch (Exception ex)
        {
            _context.Log($"MCP server '{_config.Name}' connect failed: {ex.Message}");
            _lastError = ex.Message;
            await DisposeClientCoreAsync();
            UnregisterTools();
            SetState("error");
        }
    }

    /// <summary>
    /// Registers the server's tools, diffing against the currently registered set so a changed
    /// tool list is reflected after reconnect. A name collision (another extension squatting the
    /// <c>mcp.</c> prefix, or a duplicate server) becomes a per-server error.
    /// </summary>
    private void RegisterTools(IList<McpClientTool> tools, McpToolAdapter adapter)
    {
        UnregisterTools();

        foreach (var tool in tools)
        {
            if (!McpToolAdapter.IsValidToolName(tool.Name))
            {
                _context.Log($"MCP server '{_config.Name}': skipping tool '{tool.Name}' (name violates the MCP spec).");
                continue;
            }

            var registration = adapter.ToRegistration(tool);
            try
            {
                _registeredTools[registration.Name] = _api.RegisterTool(registration);
            }
            catch (Exception ex)
            {
                _context.Log($"MCP server '{_config.Name}': failed to register tool '{registration.Name}': {ex.Message}");
                throw new InvalidOperationException($"Tool registration failed for server '{_config.Name}' (tool '{registration.Name}'): {ex.Message}", ex);
            }
        }
    }

    private void UnregisterTools()
    {
        foreach (var registration in _registeredTools.Values)
            registration.Dispose();
        _registeredTools.Clear();
    }

    private async Task ObserveCompletionAsync(McpClient client)
    {
        try
        {
            var details = await client.Completion;
            _context.Log($"MCP server '{_config.Name}' transport closed: {details.Exception?.Message ?? "no error"}");
        }
        catch (Exception ex)
        {
            _context.Log($"MCP server '{_config.Name}' completion observation failed: {ex.Message}");
        }

        if (_stopping || _disposed || !ReferenceEquals(_client, client))
            return;

        await RunReconnectLoopAsync();
    }

    private async Task RunReconnectLoopAsync()
    {
        var maxAttempts = _options.ReconnectMaxAttempts;
        if (maxAttempts <= 0)
        {
            _lastError = "Connection lost.";
            SetState("error");
            return;
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_stopping || _disposed) return;
            _reconnectAttempt = attempt;
            _lastError = "Connection lost; reconnecting.";
            SetState("reconnecting");

            var delay = TimeSpan.FromMilliseconds(
                _options.ReconnectDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            try
            {
                await Task.Delay(delay, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _gate.WaitAsync(_lifetime.Token);
            try
            {
                if (_stopping || _disposed) return;
                await ConnectCoreAsync(_lifetime.Token);
                if (_state == "connected") return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _gate.Release();
            }
        }

        if (!_stopping && !_disposed)
        {
            _lastError = $"Connection lost after {maxAttempts} reconnect attempts.";
            SetState("error");
        }
    }

    private async Task DisposeClientCoreAsync()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _context.Log($"MCP server '{_config.Name}' client disposal failed: {ex.Message}");
        }
    }

    private void SetState(string state)
    {
        if (string.Equals(_state, state, StringComparison.Ordinal)) return;
        _state = state;
        var status = GetStatus();
        _context.Log($"MCP server '{_config.Name}' state -> {state}");
        StatusChanged?.Invoke(status);
    }
}
