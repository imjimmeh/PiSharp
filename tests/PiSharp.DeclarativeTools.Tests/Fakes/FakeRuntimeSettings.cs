using PiSharp.Extensions;

namespace PiSharp.DeclarativeTools.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExtensionRuntimeSettings"/> for extension-level tests.
/// Keys are full physical paths (e.g. <c>extensions.pisharp-declarative-tools.enabled</c>)
/// exactly as the runtime settings service fires them.
/// </summary>
internal sealed class FakeRuntimeSettings : IExtensionRuntimeSettings
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<Action<ExtensionSettingsChange>> _handlers = [];

    public object? GetRaw(string path) => _values.TryGetValue(path, out var value) ? value : null;

    public Task SetRawAsync(string path, object? value, ExtensionSettingsScope scope, CancellationToken cancellationToken = default)
    {
        _values[path] = value;
        Fire(new ExtensionSettingsChange(path, value, "Fake", "fake"));
        return Task.CompletedTask;
    }

    public Task RemoveRawAsync(string path, ExtensionSettingsScope scope, CancellationToken cancellationToken = default)
    {
        _values.Remove(path);
        Fire(new ExtensionSettingsChange(path, null, "Fake", "fake"));
        return Task.CompletedTask;
    }

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
    {
        lock (_handlers) _handlers.Add(handler);
        return new ChangeSubscription(() => { lock (_handlers) _handlers.Remove(handler); });
    }

    public Task<string?> SourceLayerForAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("Fake");

    public void Fire(ExtensionSettingsChange change)
    {
        Action<ExtensionSettingsChange>[] snapshot;
        lock (_handlers) snapshot = _handlers.ToArray();
        foreach (var handler in snapshot) handler(change);
    }

    private sealed class ChangeSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
