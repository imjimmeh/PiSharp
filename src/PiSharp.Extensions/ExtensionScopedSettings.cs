using System.Text.Json;

namespace PiSharp.Extensions;

/// <summary>Normalizes an extension id into a settings/state namespace: lowercase, non [a-z0-9-] → '-', trailing '-' trimmed.</summary>
public static class ExtensionNamespaces
{
    public static string Normalize(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Extension id is required to derive a settings/state namespace.", nameof(id));
        var normalized = string.Concat(id.Trim().Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' ? ch : '-')).ToLowerInvariant();
        return normalized.TrimEnd('-');
    }
}

/// <summary>
/// Per-extension settings surface: applies the <c>extensions.&lt;namespace&gt;.</c> prefix and key
/// validation over the shared <see cref="IExtensionRuntimeSettings"/>, and claims the namespace
/// (first-writer-wins) before the first write.
/// </summary>
public sealed class ExtensionScopedSettings : IExtensionSettingsApi
{
    private readonly IExtensionRuntimeSettings? _runtime;
    private readonly string _namespace;
    private readonly string _sourceId;
    private readonly string _prefix;
    private bool _claimed;

    public ExtensionScopedSettings(ExtensionDescriptor descriptor, IExtensionRuntimeSettings? runtime)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        _runtime = runtime;
        _namespace = ExtensionNamespaces.Normalize(descriptor.Id);
        _sourceId = descriptor.EffectiveSourceId;
        _prefix = $"extensions.{_namespace}.";
    }

    public string Namespace => _namespace;

    public object? Get(string key)
    {
        ExtensionSettingKeys.ValidateSettingsKey(key);
        return _runtime?.GetRaw(_prefix + key);
    }

    public T? Get<T>(string key)
    {
        var value = Get(key);
        if (value is null) return default;
        return JsonSerializer.Deserialize<T>(ExtensionSettingKeys.ToJsonNode(value)!.ToJsonString());
    }

    public object? GetCore(string path)
    {
        ExtensionSettingKeys.ValidateSettingsKey(path);
        return _runtime?.GetRaw(path);
    }

    public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        ExtensionSettingKeys.ValidateSettingsKey(key);
        EnsureClaimed();
        return _runtime is null
            ? throw new NotSupportedException("Settings are not available: the extension runtime has no settings service bound.")
            : _runtime.SetRawAsync(_prefix + key, value, scope, cancellationToken);
    }

    public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        ExtensionSettingKeys.ValidateSettingsKey(key);
        EnsureClaimed();
        return _runtime is null
            ? throw new NotSupportedException("Settings are not available: the extension runtime has no settings service bound.")
            : _runtime.RemoveRawAsync(_prefix + key, scope, cancellationToken);
    }

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
        => OnChange(string.Empty, handler);

    public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (keyPrefix.Length > 0) ExtensionSettingKeys.ValidateSettingsKey(keyPrefix);
        if (_runtime is null) return NoopDisposable.Instance;
        var physicalPrefix = _prefix + keyPrefix;
        return _runtime.OnChange(change =>
        {
            if (change.Key.StartsWith(physicalPrefix, StringComparison.Ordinal))
                handler(change);
        });
    }

    private void EnsureClaimed()
    {
        if (_claimed) return;
        if (_runtime is IExtensionRuntimeSettingsClaimer claimer && !claimer.TryClaimNamespace(_namespace, _sourceId))
            throw new InvalidOperationException($"Settings namespace '{_namespace}' is already claimed by another extension; extension ids must normalize to distinct namespaces.");
        _claimed = true;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
