using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Core;
using PiSharp.Extensions;
using PiSharp.Tools;

[assembly: ExtensionMetadata("pisharp-debug", Name = "PiSharp debug client", Version = "1.0.0")]

namespace PiSharp.Plugins.Debug;

/// <summary>
/// <c>pisharp-debug</c> extension entry: registers the <c>debug</c> tool (DAP client facade
/// over per-session adapter processes), hot-reloads adapter configs, and sweeps idle
/// sessions. Reads <c>extensions.pisharp-debug.*</c> settings (gate, idle timeout, adapters).
/// </summary>
public sealed class DebugExtension : IExtension, IAsyncDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private ILogger _logger = NullLogger<DebugExtension>.Instance;
    private IExtensionApi? _api;
    private DebugSessionRegistry? _registry;
    private DebugToolHandler? _toolHandler;
    private IDisposable? _settingsSubscription;
    private CancellationTokenSource? _sweepCts;
    private Task? _sweepTask;
    private volatile bool _enabled;
    private volatile int _idleTimeoutMinutes = 30;
    private IReadOnlyDictionary<string, DebugAdapterConfig> _configs = new Dictionary<string, DebugAdapterConfig>();
    private bool _initialized;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _logger = NullLogger<DebugExtension>.Instance;

        ReloadSettings();
        _configs = ReadAdapterConfigs();

        _registry = new DebugSessionRegistry(
            _configs,
            api.Cwd,
            TimeSpan.FromMinutes(_idleTimeoutMinutes),
            processFactory: null);
        _toolHandler = new DebugToolHandler(_registry, () => _enabled, api.Cwd);

        _registry.SessionUpdated += session => Emit("debug_session_updated", session);
        _registry.AdapterEventReceived += (sessionId, eventName, body) =>
            Emit("debug_session_event", new { sessionId, @event = eventName, body });

        api.RegisterTool(new ExtensionToolRegistration(
            "debug",
            "debug",
            "Control a Debug Adapter Protocol session: attach, continue, pause, step, threads, stackTrace, scopes, variables, evaluate, setBreakpoints, disconnect, list. Adapters are configured per language under extensions.pisharp-debug.adapters.",
            ToolSchemas.FromType<DebugToolInput>(),
            (toolCallId, parameters, ct, _) => _toolHandler.ExecuteAsync(toolCallId, parameters, ct)));

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

    private IReadOnlyDictionary<string, DebugAdapterConfig> ReadAdapterConfigs()
    {
        if (_api is null) return new Dictionary<string, DebugAdapterConfig>();

        var adapters = _api.Settings.Get<JsonElement?>("adapters");
        if (adapters is { } element && element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any())
        {
            return ParseAdapterConfigs(element);
        }

        return ParseAdapterConfigs(DefaultAdapters);
    }

    private IReadOnlyDictionary<string, DebugAdapterConfig> ParseAdapterConfigs(JsonElement adapters)
    {
        var configs = new Dictionary<string, DebugAdapterConfig>(StringComparer.Ordinal);
        if (adapters.ValueKind != JsonValueKind.Object) return configs;

        foreach (var property in adapters.EnumerateObject())
        {
            var result = DebugAdapterConfigParser.Parse(property.Value, property.Name);
            if (result.IsOk)
            {
                configs[property.Name] = result.Value;
            }
            else
            {
                _logger.LogWarning("Ignoring invalid debug adapter config for '{Language}': {Error}", property.Name, result.Error);
            }
        }

        return configs;
    }

    private void OnSettingsChanged(ExtensionSettingsChange change)
    {
        if (!_initialized) return;

        ReloadSettings();
        var newConfigs = ReadAdapterConfigs();
        if (newConfigs.Count == _configs.Count && newConfigs.Keys.All(_configs.ContainsKey))
        {
            // Configs are immutable records; replace the map in place (sessions keep the
            // config captured at attach time). No registry rebuild needed.
            _configs = newConfigs;
            return;
        }

        _logger.LogInformation("Debug adapter configs changed; rebuilding registry.");
        var previous = _registry;
        _configs = newConfigs;
        _registry = new DebugSessionRegistry(
            _configs,
            _api?.Cwd ?? Environment.CurrentDirectory,
            TimeSpan.FromMinutes(_idleTimeoutMinutes),
            processFactory: null);
        _registry.SessionUpdated += session => Emit("debug_session_updated", session);
        _registry.AdapterEventReceived += (sessionId, eventName, body) =>
            Emit("debug_session_event", new { sessionId, @event = eventName, body });
        _toolHandler?.SwapRegistry(_registry);
        _ = previous?.DisposeAsync();
    }

    private void Emit(string eventName, object payload)
    {
        var api = _api;
        if (api is null) return;
        try
        {
            api.Events.EmitAsync(eventName, payload).GetAwaiter().GetResult();
        }
        catch (NotSupportedException)
        {
            // Events surface is optional for hosts that do not support emission.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to emit extension event '{Event}'.", eventName);
        }
    }

    private async Task RunIdleSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, cancellationToken).ConfigureAwait(false);
                var registry = _registry;
                if (registry is not null)
                {
                    await registry.DisposeIdleAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _settingsSubscription?.Dispose();
        if (_sweepCts is not null)
        {
            await _sweepCts.CancelAsync().ConfigureAwait(false);
            _sweepCts.Dispose();
        }

        if (_sweepTask is not null)
        {
            try
            {
                await _sweepTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled above.
            }
        }

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static readonly JsonElement DefaultAdapters = JsonDocument.Parse(
        """
        {
          "python": {
            "command": ["python", "-m", "debugpy.adapter"],
            "extensions": [".py"],
            "attach": { "listen": { "host": "127.0.0.1", "port": 5678 }, "pathMappings": [{ "localRoot": "${cwd}", "remoteRoot": "${path}" }] }
          },
          "go": {
            "command": ["dlv-dap", "--listen=127.0.0.1:0", "--log-dest=2"],
            "extensions": [".go"]
          },
          "cpp": {
            "command": ["lldb-dap"],
            "extensions": [".c", ".cc", ".cpp", ".h", ".hpp"]
          },
          "csharp": {
            "command": ["dotnet", "/usr/share/dotnet/vsdbg/vsdbg"],
            "extensions": [".cs"]
          }
        }
        """).RootElement.Clone();
}
