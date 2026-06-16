using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.Runtime;

public enum ExtensionLoadState
{
    Discovered,
    DescriptorReplayed,
    Pending,
    Loading,
    BackgroundLoading,
    Ready,
    Failed,
    Stale,
    Disabled
}

public sealed record ExtensionLoadStatus(
    string ExtensionPath,
    ExtensionLoadState State,
    string? Diagnostic = null,
    int Generation = 0);

public sealed class ExtensionLoadCoordinator : IAsyncDisposable
{
    private const int PerExtensionLoadTimeoutMinutes = 3;

    private readonly ConcurrentDictionary<string, ExtensionLoadStatus> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<TsExtensionLoadResult>> _loads = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private int _generation;
    private readonly ILogger _logger;

    public ExtensionLoadCoordinator(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ExtensionLoadCoordinator>() ?? NullLogger<ExtensionLoadCoordinator>.Instance;
    }

    public IReadOnlyList<ExtensionLoadStatus> Statuses => _states.Values.OrderBy(status => status.ExtensionPath, StringComparer.Ordinal).ToArray();
    public Task ExtensionsReadyTask => Task.WhenAll(_loads.Values);

    public void MarkDiscovered(string extensionPath) => Set(extensionPath, ExtensionLoadState.Discovered);
    public void MarkDescriptorReplayed(string extensionPath) => Set(extensionPath, ExtensionLoadState.DescriptorReplayed);
    public void MarkPending(string extensionPath) => Set(extensionPath, ExtensionLoadState.Pending);
    public void MarkLoading(string extensionPath) => Set(extensionPath, ExtensionLoadState.Loading);
    public void MarkBackgroundLoading(string extensionPath) => Set(extensionPath, ExtensionLoadState.BackgroundLoading);
    public void MarkReady(string extensionPath) => Set(extensionPath, ExtensionLoadState.Ready);
    public void MarkFailed(string extensionPath, string? diagnostic = null) => Set(extensionPath, ExtensionLoadState.Failed, diagnostic);
    public void MarkStale(string extensionPath) => Set(extensionPath, ExtensionLoadState.Stale);

    public Task<TsExtensionLoadResult> RunOnceAsync(string extensionPath, Func<CancellationToken, Task<TsExtensionLoadResult>> loadAsync, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug($"ext: queue {ShortPath(extensionPath)}");
        return _loads.GetOrAdd(extensionPath, _ => RunAsync(extensionPath, ExtensionLoadState.Loading, loadAsync, cancellationToken));
    }

    public Task<TsExtensionLoadResult> RunInBackgroundAsync(string extensionPath, Func<CancellationToken, Task<TsExtensionLoadResult>> loadAsync, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug($"ext: queue background {ShortPath(extensionPath)}");
        return _loads.GetOrAdd(extensionPath, _ => RunAsync(extensionPath, ExtensionLoadState.BackgroundLoading, loadAsync, cancellationToken));
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        var activeLoads = _loads.Values.ToArray();
        try
        {
            await Task.WhenAll(activeLoads).WaitAsync(cancellationToken);
        }
        catch
        {
            // Existing load diagnostics are already captured in status entries; reload should still reset the generation.
        }

        Interlocked.Increment(ref _generation);
        _loads.Clear();
        foreach (var key in _states.Keys) _states[key] = _states[key] with { State = ExtensionLoadState.Stale, Generation = _generation };
    }

    private async Task<TsExtensionLoadResult> RunAsync(string extensionPath, ExtensionLoadState loadingState, Func<CancellationToken, Task<TsExtensionLoadResult>> loadAsync, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(PerExtensionLoadTimeoutMinutes));
        _logger.LogDebug($"ext: load {ShortPath(extensionPath)}");
        Set(extensionPath, loadingState);
        try
        {
            var result = await loadAsync(linked.Token);
            if (result.Ok)
            {
                _logger.LogDebug($"ext: ok   {ShortPath(extensionPath)}");
                MarkReady(extensionPath);
            }
            else
            {
                _logger.LogDebug($"ext: FAIL {ShortPath(extensionPath)} — {result.Error}");
                MarkFailed(extensionPath, result.Error);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug($"ext: TIMEOUT {ShortPath(extensionPath)}");
            MarkFailed(extensionPath, $"Extension load timed out after {PerExtensionLoadTimeoutMinutes} minutes.");
            return new TsExtensionLoadResult(false, extensionPath, $"Extension load timed out after {PerExtensionLoadTimeoutMinutes} minutes.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug($"ext: FAIL {ShortPath(extensionPath)} — {exception.Message}");
            MarkFailed(extensionPath, exception.Message);
            return new TsExtensionLoadResult(false, extensionPath, exception.Message);
        }
    }

    private static string ShortPath(string path)
    {
        var dirName = Path.GetDirectoryName(path);
        var baseName = Path.GetFileName(path);
        return dirName is null ? baseName : $"{Path.GetFileName(dirName)}\\{baseName}";
    }

    private void Set(string extensionPath, ExtensionLoadState state, string? diagnostic = null)
        => _states[extensionPath] = new ExtensionLoadStatus(extensionPath, state, diagnostic, _generation);

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        try { await Task.WhenAll(_loads.Values.ToArray()); }
        catch { /* diagnostics are recorded in statuses */ }
        _disposeCts.Dispose();
    }
}
