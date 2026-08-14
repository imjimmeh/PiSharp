using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;

namespace PiSharp.Runtime;

/// <summary>
/// Runtime settings core shared by all extensions: owns the <see cref="PiSettingsStore"/> snapshot,
/// serializes writes behind an in-process gate, and publishes every committed change to the
/// in-process <c>OnChange</c> subscribers and the <c>settings_changed</c> extension event.
/// Writes target only PiSharp layers — provenance resolving to a legacy layer is mapped to its
/// PiSharp sibling for <c>extensions.*</c> paths; core paths keep provenance semantics
/// (fallback <see cref="PiSettingsLayer.GlobalLegacy"/>), preserving CLI model-persistence behavior.
/// </summary>
public sealed class ExtensionSettingsService : IExtensionRuntimeSettings, IExtensionRuntimeSettingsClaimer, IDisposable
{
    private readonly PiSettingsStore? _store;
    private PiSettingsSnapshot? _snapshot;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<Action<ExtensionSettingsChange>> _onChange = [];
    private readonly ConcurrentDictionary<string, string> _namespaceClaims = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExtensionEventBus? _bus;

    public ExtensionSettingsService(
        PiSettingsStore? store,
        PiSettingsSnapshot? snapshot,
        ExtensionRegistry? registry = null,
        ILoggerFactory? loggerFactory = null)
    {
        _store = store;
        _snapshot = snapshot;
        _logger = loggerFactory?.CreateLogger<ExtensionSettingsService>() ?? NullLogger<ExtensionSettingsService>.Instance;
        _bus = registry is null ? null : new ExtensionEventBus(registry, "runtime:settings", loggerFactory: loggerFactory);
    }

    /// <summary>True when a settings store and snapshot are bound (production runtime); false in headless/test construction.</summary>
    public bool IsAvailable => _store is not null && _snapshot is not null;

    /// <summary>Current merged snapshot; refreshes after every committed write.</summary>
    internal PiSettingsSnapshot? CurrentSnapshot => _snapshot;

    public object? GetRaw(string path)
    {
        ExtensionSettingKeys.ValidateSettingsKey(path);
        var snapshot = _snapshot;
        if (snapshot is null) return null;
        JsonNode? node = snapshot.Merged.Root;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject container || !container.TryGetPropertyValue(segment, out node) || node is null) return null;
        }
        return ExtensionSettingKeys.FromJsonNode(node);
    }

    public Task SetRawAsync(string path, object? value, ExtensionSettingsScope scope, CancellationToken cancellationToken = default)
        => SetRawCoreAsync(path, value, ResolveLayerFor(path, scope), SourceIdFor(path), cancellationToken);

    /// <summary>Same as the interface overload but with an explicit writer identity (e.g. "runtime:model").</summary>
    public Task SetRawAsync(string path, object? value, ExtensionSettingsScope scope, string sourceId, CancellationToken cancellationToken = default)
        => SetRawCoreAsync(path, value, ResolveLayerFor(path, scope), sourceId, cancellationToken);

    /// <summary>Writes to an explicitly pinned layer, bypassing scope resolution. Used by the model controller.</summary>
    public Task SetRawOnLayerAsync(string path, object? value, PiSettingsLayer layer, string sourceId, CancellationToken cancellationToken = default)
        => SetRawCoreAsync(path, value, layer, sourceId, cancellationToken);

    public Task RemoveRawAsync(string path, ExtensionSettingsScope scope, CancellationToken cancellationToken = default)
        => SetRawAsync(path, null, scope, cancellationToken);

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_onChange) _onChange.Add(handler);
        return new OnChangeSubscription(() =>
        {
            lock (_onChange) _onChange.Remove(handler);
        });
    }

    public Task<string?> SourceLayerForAsync(string path, CancellationToken cancellationToken = default)
    {
        ExtensionSettingKeys.ValidateSettingsKey(path);
        return Task.FromResult<string?>(ResolveLayerFor(path, ExtensionSettingsScope.Source).ToString());
    }

    public bool TryClaimNamespace(string extensionNamespace, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(extensionNamespace))
            throw new ArgumentException("Extension namespace is required.", nameof(extensionNamespace));
        return _namespaceClaims.TryAdd(extensionNamespace, sourceId);
    }

    public void Dispose() => _writeGate.Dispose();

    private async Task SetRawCoreAsync(string path, object? value, PiSettingsLayer layer, string sourceId, CancellationToken cancellationToken)
    {
        ExtensionSettingKeys.ValidateSettingsKey(path);
        var store = _store;
        if (store is null || _snapshot is null) return; // headless construction: writes are inert

        var jsonNode = ExtensionSettingKeys.ToJsonNode(value); // ArgumentException for non-JSON values

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _snapshot;
            await store.SaveLayerAsync(snapshot, layer, document => SetPath(document.Root, path, jsonNode), cancellationToken).ConfigureAwait(false);
            _snapshot = await store.LoadAsync(snapshot.Paths.Cwd, snapshot.Paths.HomeDirectory, snapshot.Paths.Profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        var change = new ExtensionSettingsChange(path, GetRaw(path), layer.ToString(), sourceId);
        FireOnChange(change);
        await PublishEventAsync(change, cancellationToken).ConfigureAwait(false);
    }

    private void FireOnChange(ExtensionSettingsChange change)
    {
        Action<ExtensionSettingsChange>[] subscribers;
        lock (_onChange) subscribers = _onChange.ToArray();
        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(change);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Settings change subscriber failed for key {Key}", change.Key);
            }
        }
    }

    private async Task PublishEventAsync(ExtensionSettingsChange change, CancellationToken cancellationToken)
    {
        if (_bus is null) return;
        try
        {
            await _bus.EmitAsync(ExtensionEventNames.SettingsChanged, change, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "settings_changed event publish failed for key {Key}", change.Key);
        }
    }

    private PiSettingsLayer ResolveLayerFor(string path, ExtensionSettingsScope scope)
    {
        if (scope == ExtensionSettingsScope.Global) return PiSettingsLayer.GlobalPiSharp;
        if (scope == ExtensionSettingsScope.Project) return PiSettingsLayer.ProjectPiSharp;

        var isExtensionPath = path.StartsWith("extensions.", StringComparison.Ordinal);
        var snapshot = _snapshot;
        if (snapshot is null) return isExtensionPath ? PiSettingsLayer.GlobalPiSharp : PiSettingsLayer.GlobalLegacy;

        var container = isExtensionPath ? "extensions" : path.Split('.')[0];
        var layer = snapshot.SourceLayerFor(container);
        if (layer is null) return isExtensionPath ? PiSettingsLayer.GlobalPiSharp : PiSettingsLayer.GlobalLegacy;

        // Extension settings never write legacy Pi files; map to the PiSharp sibling layer.
        var resolved = layer.Value;
        return resolved switch
        {
            PiSettingsLayer.GlobalLegacy => PiSettingsLayer.GlobalPiSharp,
            PiSettingsLayer.ProjectLegacy => PiSettingsLayer.ProjectPiSharp,
            _ => resolved
        };
    }

    private static string SourceIdFor(string path)
        => path.StartsWith("extensions.", StringComparison.Ordinal) ? "extension:" + path.Split('.')[1] : "runtime:settings";

    private static void SetPath(JsonObject root, string path, JsonNode? node)
    {
        var segments = path.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current[segment] is not JsonObject next)
            {
                next = [];
                current[segment] = next;
            }
            current = next;
        }

        var last = segments[^1];
        if (node is null) current.Remove(last);
        else current[last] = node;
    }

    private sealed class OnChangeSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
