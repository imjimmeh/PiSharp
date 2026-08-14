using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

namespace PiSharp.Mcp;

/// <summary>
/// Orchestrates the MCP client plugin: reads server configuration from extension settings
/// (<c>extensions.pisharp-mcp.mcpServers</c>), reconciles live <see cref="McpServerSession"/>s by
/// diff against the desired set (settings + extension-contributed servers), subscribes to settings
/// changes (debounced), registers the <c>/mcp</c> slash command, and emits
/// <c>mcp_server_status</c> events on state transitions.
/// </summary>
public sealed class McpClientHost : IDisposable
{
    private readonly IExtensionApi _api;
    private readonly McpTransportContext _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, McpServerSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _configErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _explicitlyConnected = new(StringComparer.Ordinal);
    private readonly Func<McpTransportKind, IMcpTransportFactory?> _factoryResolver;
    private readonly TimeSpan _settingsDebounce;

    private McpHostOptions _options = McpHostOptions.Default;
    private IDisposable? _settingsSubscription;
    private IDisposable? _commandRegistration;
    private CancellationTokenSource? _debounce;
    private bool _started;
    private bool _disposed;

    public McpClientHost(IExtensionApi api, McpTransportContext context,
        Func<McpTransportKind, IMcpTransportFactory?>? factoryResolver = null)
        : this(api, context, TimeSpan.FromMilliseconds(250), factoryResolver)
    {
    }

    internal McpClientHost(IExtensionApi api, McpTransportContext context, TimeSpan settingsDebounce,
        Func<McpTransportKind, IMcpTransportFactory?>? factoryResolver = null)
    {
        _api = api;
        _context = context;
        _settingsDebounce = settingsDebounce;
        _factoryResolver = factoryResolver ?? McpServerRegistry.FindFactory;
    }

    public event Action<McpServerStatus>? StatusChanged;

    /// <summary>Loads settings, subscribes to changes, registers <c>/mcp</c>, and auto-connects.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _options = McpHostOptions.FromSettings(_api.Settings);
        _settingsSubscription = _api.Settings.OnChange(OnSettingsChanged);
        _commandRegistration = _api.RegisterCommand(new ExtensionCommandRegistration(
            "mcp",
            "Manage MCP servers (list, status, connect, disconnect, reload, login, logout).",
            (args, ct) => new McpSlashCommand(this, _api).HandleAsync(args, ct)));
        _started = true;
        if (_options.Enabled && _options.AutoConnect)
            await ReconcileAsync(cancellationToken);
    }

    /// <summary>Diffs the desired server set (settings + contributions) against live sessions.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previousOptions = _options;
            _options = McpHostOptions.FromSettings(_api.Settings);
            var desired = BuildDesiredServers();
            var restartAll = _options.ToolPrefix != previousOptions.ToolPrefix
                || _options.MaxToolResultLines != previousOptions.MaxToolResultLines
                || _options.MaxToolResultBytes != previousOptions.MaxToolResultBytes;

            // Stop servers that disappeared or were disabled.
            foreach (var name in _sessions.Keys.ToArray())
            {
                if (!desired.TryGetValue(name, out _))
                {
                    _context.Log($"MCP: removing server '{name}' (no longer configured).");
                    await StopSessionAsync(name);
                }
            }

            // Restart changed servers and start new ones.
            foreach (var (name, config) in desired)
            {
                if (!_sessions.TryGetValue(name, out var session))
                {
                    if (!ShouldAutoStart(name)) continue;
                    _context.Log($"MCP: starting server '{name}'.");
                    await StartSessionAsync(config, cancellationToken);
                    continue;
                }

                var changed = !session.Config.Equals(config) || restartAll;
                if (!changed) continue;
                _context.Log($"MCP: restarting server '{name}' (configuration changed).");
                await StopSessionAsync(name);
                if (ShouldAutoStart(name))
                    await StartSessionAsync(config, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops all sessions and releases subscriptions.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _debounce?.Cancel();
            _settingsSubscription?.Dispose();
            _settingsSubscription = null;
            _commandRegistration?.Dispose();
            _commandRegistration = null;
            foreach (var name in _sessions.Keys.ToArray())
                await StopSessionAsync(name);
            _started = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpServerStatus> GetStatusAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(name, out var session)) return session.GetStatus();
            if (_configErrors.TryGetValue(name, out var error))
                return new McpServerStatus(name, "settings", "error", LastError: error);
            var desired = BuildDesiredServers();
            return desired.TryGetValue(name, out var config)
                ? new McpServerStatus(config.Name, config.Source, "disconnected")
                : new McpServerStatus(name, "settings", "error", LastError: $"Unknown MCP server '{name}'.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpServerStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var statuses = new List<McpServerStatus>();
            foreach (var session in _sessions.Values.OrderBy(session => session.Name, StringComparer.Ordinal))
                statuses.Add(session.GetStatus());
            var desired = BuildDesiredServers();
            foreach (var (name, config) in desired.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (_sessions.ContainsKey(name)) continue;
                statuses.Add(_configErrors.TryGetValue(name, out var error)
                    ? new McpServerStatus(name, config.Source, "error", LastError: error)
                    : new McpServerStatus(name, config.Source, "disconnected"));
            }
            return statuses;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects a configured server on demand (<c>/mcp connect</c>), regardless of autoConnect.</summary>
    public async Task<bool> ConnectAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = FindConfig(name);
            if (config is null)
            {
                _configErrors[name] = $"Unknown MCP server '{name}'.";
                return false;
            }
            _explicitlyConnected.Add(name);
            if (_sessions.TryGetValue(name, out var existing) && existing.GetStatus().State == "connected")
                return true;
            await StopSessionAsync(name);
            await StartSessionAsync(config, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Disconnects a live server (<c>/mcp disconnect</c>).</summary>
    public async Task<bool> DisconnectAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.Remove(name, out var session))
            {
                _configErrors[name] = $"Unknown MCP server '{name}'.";
                return false;
            }
            _explicitlyConnected.Remove(name);
            await session.DisposeAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the OAuth login flow for a server (<c>/mcp login</c>).</summary>
    public async Task<McpLoginResult> LoginAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_context.AuthStorage is null)
            return new McpLoginResult(false, "No auth store is available; OAuth login is not supported in this runtime.");

        var config = await FindConfigAsync(name, cancellationToken);
        if (config is null) return new McpLoginResult(false, $"Unknown MCP server '{name}'.");
        if (config.Auth?.Kind != McpAuthKind.OAuth)
            return new McpLoginResult(false, $"Server '{name}' does not use OAuth authentication.");

        // Clearing stored credentials forces the SDK to re-run the interactive flow on next connect.
        await _context.AuthStorage.RemoveTokenAsync(OAuthCredentialProvider.ProviderKey(name), cancellationToken);
        return new McpLoginResult(true, $"OAuth login for '{name}' will run on the next connect. Use '/mcp connect {name}' to start it.");
    }

    /// <summary>Removes stored OAuth credentials (<c>/mcp logout</c>).</summary>
    public async Task<McpLoginResult> LogoutAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_context.AuthStorage is null)
            return new McpLoginResult(false, "No auth store is available.");
        await _context.AuthStorage.RemoveTokenAsync(OAuthCredentialProvider.ProviderKey(name), cancellationToken);
        return new McpLoginResult(true, $"Removed stored credentials for '{name}'.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _gate.Dispose();
    }

    private IReadOnlyDictionary<string, McpServerConfig> BuildDesiredServers()
    {
        var desired = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        if (!_options.Enabled) return desired;

        foreach (var (name, config) in _options.Servers ?? new Dictionary<string, McpServerConfig>())
        {
            if (!config.Enabled) continue;
            if (config.Validate(out var error))
            {
                desired[name] = config;
                _configErrors.Remove(name);
            }
            else
            {
                _configErrors[name] = error;
            }
        }

        foreach (var contributed in McpServerRegistry.GetContributedServers())
        {
            var name = McpServerConfig.NormalizeName(contributed.Name);
            if (desired.ContainsKey(name))
            {
                _context.Log($"MCP: rejecting contributed server '{name}' (settings configuration wins).");
                continue;
            }
            var withSource = contributed with { Name = name, Source = $"extension:{contributed.Source}" };
            if (withSource.Validate(out var error))
            {
                desired[name] = withSource;
                _configErrors.Remove(name);
            }
            else
            {
                _configErrors[name] = error;
            }
        }

        return desired;
    }

    private bool ShouldAutoStart(string name)
        => _options.AutoConnect || _explicitlyConnected.Contains(name);


    private async Task StartSessionAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var session = new McpServerSession(config, _context, _api, _options, _factoryResolver);
        session.StatusChanged += OnSessionStatusChanged;
        _sessions[config.Name] = session;
        await session.ConnectAsync(cancellationToken);
    }

    private async Task StopSessionAsync(string name)
    {
        if (!_sessions.Remove(name, out var session)) return;
        session.StatusChanged -= OnSessionStatusChanged;
        await session.DisposeAsync();
    }

    private void OnSessionStatusChanged(McpServerStatus status)
    {
        StatusChanged?.Invoke(status);
        _ = _api.Events.EmitAsync(McpEventNames.McpServerStatus, status, CancellationToken.None);
    }

    private McpServerConfig? FindConfig(string name)
    {
        foreach (var config in BuildDesiredServers().Values)
            if (string.Equals(config.Name, name, StringComparison.Ordinal)) return config;
        return null;
    }

    private async Task<McpServerConfig?> FindConfigAsync(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return FindConfig(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSettingsChanged(ExtensionSettingsChange change)
    {
        if (_disposed || !_started) return;
        _debounce?.Cancel();
        var debounce = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settingsDebounce, debounce.Token);
                await ReconcileAsync(debounce.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer change superseded this one.
            }
            catch (Exception ex)
            {
                _context.Log($"MCP: settings reconcile failed: {ex.Message}");
            }
        }, CancellationToken.None);
    }
}

/// <summary>Outcome of <c>/mcp login</c>/<c>logout</c>.</summary>
public sealed record McpLoginResult(bool Success, string Message);
