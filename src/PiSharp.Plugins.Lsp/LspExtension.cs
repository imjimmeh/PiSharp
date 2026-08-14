using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Extensions;
using PiSharp.Tools;

[assembly: ExtensionMetadata("pisharp-lsp", Name = "PiSharp LSP client", Version = "1.0.0")]

namespace PiSharp.Plugins.Lsp;

/// <summary>
/// <c>pisharp-lsp</c> extension entry: registers the <c>lsp</c> tool, the post-write
/// diagnostics middleware, and the per-language server muxer. Reads
/// <c>extensions.pisharp-lsp.*</c> settings (gate, servers map, timeouts) via
/// <c>IExtensionApi.Settings</c> and hot-reloads on changes.
/// </summary>
public sealed class LspExtension : IExtension, IAsyncDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private ILogger _logger = NullLogger<LspExtension>.Instance;
    private IExtensionApi? _api;
    private LspServerRegistry? _registry;
    private LspDiagnosticsService? _diagnostics;
    private LspToolHandler? _toolHandler;
    private IDisposable? _settingsSubscription;
    private CancellationTokenSource? _sweepCts;
    private Task? _sweepTask;
    private volatile bool _enabled;
    private volatile int _idleTimeoutMinutes = 30;
    private IReadOnlyDictionary<string, LanguageServerConfig> _configs = new Dictionary<string, LanguageServerConfig>();
    private bool _initialized;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _logger = NullLogger<LspExtension>.Instance;

        ReloadSettings();
        _configs = ReadServerConfigs();

        _registry = new LspServerRegistry(
            _configs,
            api.Cwd,
            TimeSpan.FromMinutes(_idleTimeoutMinutes),
            processFactory: null);
        _diagnostics = new LspDiagnosticsService(_registry);
        _toolHandler = new LspToolHandler(_registry, () => _enabled, api.Cwd);

        _registry.ServerStatusChanged += OnServerStatusChanged;

        api.RegisterTool(new ExtensionToolRegistration(
            "lsp",
            "lsp",
            "Query a language server (hover, definition, references, rename, diagnostics, symbols, code actions, format) or send a raw request. Language servers are configured per language under extensions.pisharp-lsp.servers.",
            ToolSchemas.FromType<LspToolInput>(),
            (toolCallId, parameters, ct, _) => _toolHandler.ExecuteAsync(toolCallId, parameters, ct)));

        var middleware = new LspDiagnosticsMiddleware(_registry, _diagnostics);
        api.Use(middleware.HandleAsync);

        _settingsSubscription = api.Settings.OnChange(OnSettingsChanged);

        _sweepCts = new CancellationTokenSource();
        _sweepTask = RunIdleSweepAsync(_sweepCts.Token);
        _initialized = true;
    }

    private void ReloadSettings()
    {
        var api = _api;
        if (api is null) return;
        _enabled = api.Settings.Get<bool>("enabled");
        _idleTimeoutMinutes = api.Settings.Get<int>("idleTimeoutMinutes");
    }

    private IReadOnlyDictionary<string, LanguageServerConfig> ReadServerConfigs()
    {
        if (_api is null) return new Dictionary<string, LanguageServerConfig>();

        var servers = _api.Settings.Get<JsonElement?>("servers");
        if (servers is { } element && element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any())
        {
            return ParseServerConfigs(element);
        }

        return ParseServerConfigs(DefaultServers);
    }

    private IReadOnlyDictionary<string, LanguageServerConfig> ParseServerConfigs(JsonElement servers)
    {
        var configs = new Dictionary<string, LanguageServerConfig>(StringComparer.Ordinal);
        if (servers.ValueKind != JsonValueKind.Object) return configs;

        foreach (var property in servers.EnumerateObject())
        {
            var result = LanguageServerConfigParser.Parse(property.Value, property.Name);
            if (result.IsOk)
            {
                configs[property.Name] = result.Value;
            }
            else
            {
                _logger.LogWarning("Ignoring invalid language server config for '{Language}': {Error}", property.Name, result.Error);
            }
        }

        return configs;
    }

    private void OnSettingsChanged(ExtensionSettingsChange change)
    {
        if (!_initialized) return;

        ReloadSettings();
        var newConfigs = ReadServerConfigs();
        if (ConfigsEqual(newConfigs, _configs))
        {
            // Only gate/timeouts changed; the registry already reads _enabled/_idleTimeoutMinutes live.
            return;
        }

        // Config map changed: swap the registry (the previous one disposes its servers).
        var previous = _registry;
        _configs = newConfigs;
        _registry = new LspServerRegistry(
            _configs,
            _api!.Cwd,
            TimeSpan.FromMinutes(_idleTimeoutMinutes),
            processFactory: null);
        _diagnostics = new LspDiagnosticsService(_registry);
        _toolHandler = new LspToolHandler(_registry, () => _enabled, _api.Cwd);
        _registry.ServerStatusChanged += OnServerStatusChanged;
        _ = previous?.DisposeAsync();
    }

    private static bool ConfigsEqual(IReadOnlyDictionary<string, LanguageServerConfig> left, IReadOnlyDictionary<string, LanguageServerConfig> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (language, config) in left)
        {
            if (!right.TryGetValue(language, out var other) || !config.Command.SequenceEqual(other.Command)) return false;
        }

        return true;
    }

    private void OnServerStatusChanged(LspServerStatus status)
    {
        Emit("lsp_server_status_changed", status);
    }

    private void Emit(string eventName, object payload)
    {
        if (_api is null) return;
        try
        {
            _ = _api.Events.EmitAsync(eventName, payload, CancellationToken.None);
        }
        catch (Exception exception) when (exception is NotSupportedException or ObjectDisposedException)
        {
            // Fake/standalone host without an event bus: events are optional.
        }
    }

    private async Task RunIdleSweepAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, cancellationToken).ConfigureAwait(false);
                if (_registry is not null)
                {
                    await _registry.DisconnectIdleAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "LSP idle sweep failed.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sweepCts is not null)
        {
            await _sweepCts.CancelAsync();
            if (_sweepTask is not null)
            {
                try { await _sweepTask; }
                catch (OperationCanceledException) { }
            }

            _sweepCts.Dispose();
        }

        _settingsSubscription?.Dispose();
        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static readonly JsonElement DefaultServers = JsonDocument.Parse(
        """
        {
          "typescript": { "command": ["typescript-language-server", "--stdio"], "extensions": [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"] },
          "rust": { "command": ["rust-analyzer"], "extensions": [".rs"] },
          "python": { "command": ["pyright-langserver", "--stdio"], "extensions": [".py"] },
          "csharp": { "command": ["dotnet", "omnisharp"], "extensions": [".cs"] }
        }
        """).RootElement.Clone();
}
