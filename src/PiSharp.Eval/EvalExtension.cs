using PiSharp.Abstractions.Messages;
using PiSharp.Eval.Bench;
using PiSharp.Eval.Commands;
using PiSharp.Eval.Events;
using PiSharp.Eval.Kernels;
using PiSharp.Eval.Tools;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-eval", Name = "PiSharp Eval & Bench", Version = "1.0.0")]

namespace PiSharp.Eval;

/// <summary>
/// <c>pisharp-eval</c> extension entry: registers the <c>eval</c> tool, the <c>/kernel</c>
/// and <c>/bench</c> slash commands, and the lifecycle hooks
/// (<c>session_before_compact</c>, <c>compaction_start</c>, <c>session_before_fork</c>,
/// <c>session_shutdown</c>, <c>save_point</c>). Owns the per-session
/// <see cref="KernelRegistry"/> and <see cref="BenchRunner"/>; snapshots and disposes all
/// kernels on shutdown/unload.
/// </summary>
public sealed class EvalExtension : IExtension, IAsyncDisposable
{
    private IExtensionApi? _api;
    private KernelRegistry? _registry;
    private KernelSnapshotStore? _store;
    private KernelToolBridge? _bridge;
    private EvalOptions _options = new();
    private string _sessionId = "default";
    private readonly List<IDisposable> _subscriptions = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile bool _disposed;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _options = EvalOptions.Read(api, api.Cwd);
        _sessionId = await GetSessionIdAsync(api, cancellationToken);

        _store = new KernelSnapshotStore(api.State, maxBytes: _options.SnapshotMaxBytes);
        _bridge = new KernelToolBridge("eval", api.Tools, Emit, _options.LoopbackTools);
        _registry = new KernelRegistry(api.Cwd, _bridge, new KernelRegistryOptions
        {
            SessionId = _sessionId,
            RestoreOnStart = _options.RestoreOnStart,
            SnapshotProvider = (kernelName, sessionId, ct) => _store.LoadAsync(sessionId, kernelName, ct),
        });

        var evalTool = new EvalTool(_registry, Emit, _sessionId, _options.DefaultKernel, _options.KernelTimeoutMs);
        api.RegisterTool(evalTool.ToRegistration());

        var kernelCommand = new KernelSlashCommand(_registry, _store, Emit, _sessionId);
        api.RegisterCommand(new ExtensionCommandRegistration(
            "kernel",
            "Inspect, reset, snapshot, or restore eval kernels: /kernel [reset|snapshot|restore] [kernelName].",
            (args, ct) => RunCommandAsync(kernelCommand.HandleAsync, args, ct)));

        var writer = new BenchResultWriter(_options.BenchDir);
        var runner = new BenchRunner(api.Completion, _registry, writer, Emit, api.Cwd,
            _options.BenchProvider, _options.BenchModel, _options.KernelTimeoutMs);
        var benchCommand = new BenchSlashCommand(runner, _options.BenchDir);
        api.RegisterCommand(new ExtensionCommandRegistration(
            "bench",
            "Run a repeatable scored eval bench: /bench [spec-path] [--runs N].",
            (args, ct) => RunCommandAsync(benchCommand.HandleAsync, args, ct)));

        // Lifecycle hooks: snapshot kernel state before anything that can tear the runtime
        // down (compaction, fork, shutdown); restore-on-start rehydrates from the snapshot
        // when a re-created runtime carries the same session id (P01 recovery path).
        _subscriptions.Add(api.On(ExtensionEventNames.SessionBeforeCompact, (_, ct) => SnapshotAllAsync("compact", ct)));
        _subscriptions.Add(api.On(ExtensionEventNames.CompactionStart, (_, ct) => SnapshotAllAsync("compact", ct)));
        _subscriptions.Add(api.On(ExtensionEventNames.SessionBeforeFork, (_, ct) => SnapshotAllAsync("fork", ct)));
        _subscriptions.Add(api.On(ExtensionEventNames.SessionShutdown, (_, ct) => ShutdownAsync(ct)));
        _subscriptions.Add(api.On(ExtensionEventNames.SavePoint, (_, ct) => SnapshotAllAsync("save_point", ct)));
        _subscriptions.Add(api.On(ExtensionEventNames.SettingsChanged, (_, _) => { ReloadSettings(); return Task.CompletedTask; }));
    }

    private async Task RunCommandAsync(
        Func<string, CancellationToken, Task<string>> handler,
        string args,
        CancellationToken ct)
    {
        var text = await handler(args, ct);
        if (_api is not null)
        {
            await _api.SendMessageAsync(AgentMessages.User(text), ct);
        }
    }

    private async Task<string> GetSessionIdAsync(IExtensionApi api, CancellationToken ct)
    {
        try
        {
            var name = await api.Session.GetNameAsync(ct);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception)
        {
            // Session name unavailable — fall back to "default".
        }
        return "default";
    }

    private void ReloadSettings()
    {
        if (_api is null) return;
        var updated = EvalOptions.Read(_api, _api.Cwd);
        _options = updated;
        _bridge?.UpdateAllowlist(updated.LoopbackTools);
    }

    /// <summary>Snapshot every running kernel (compaction/fork/save_point hooks).</summary>
    private async Task SnapshotAllAsync(string reason, CancellationToken ct)
    {
        if (_registry is null || _store is null || _disposed) return;
        if (reason == "save_point" && _options.SavePointIntervalMs <= 0) return;

        foreach (var kernel in _registry.Kernels.ToArray())
        {
            try
            {
                var snapshot = await kernel.SnapshotAsync(ct);
                await _store.SaveAsync(_sessionId, kernel.Name, snapshot, ct);
                await Emit(EvalEventNames.Snapshot,
                    new EvalSnapshotEvent(kernel.Name, _sessionId, snapshot.Lossy, snapshot.Variables.Count, 0), ct);
            }
            catch (Exception)
            {
                // A failed best-effort snapshot must not break the lifecycle hook.
            }
        }
    }

    /// <summary>Final snapshot + dispose all kernels (session_shutdown / unload).</summary>
    private async Task ShutdownAsync(CancellationToken ct)
    {
        if (_disposed) return;
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var sub in _subscriptions) sub.Dispose();
            _subscriptions.Clear();

            if (_registry is not null)
            {
                await _registry.DisposeAllAsync(async (kernel, token) =>
                {
                    if (_store is null) return;
                    try
                    {
                        var snapshot = await kernel.SnapshotAsync(token);
                        await _store.SaveAsync(_sessionId, kernel.Name, snapshot, token);
                        await Emit(EvalEventNames.Snapshot,
                            new EvalSnapshotEvent(kernel.Name, _sessionId, snapshot.Lossy, snapshot.Variables.Count, 0), token);
                    }
                    catch (Exception)
                    {
                        // Final snapshot is best-effort.
                    }
                }, ct);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task Emit(string eventName, object payload, CancellationToken ct)
    {
        if (_api is null) return;
        try
        {
            await _api.Events.EmitAsync(eventName, payload, ct);
        }
        catch (Exception)
        {
            // Event emission must never break the extension path that triggered it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None);
    }
}
