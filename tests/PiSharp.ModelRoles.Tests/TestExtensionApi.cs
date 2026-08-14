using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.ModelRoles.Tests;

/// <summary>
/// Minimal in-memory <see cref="IExtensionSettingsApi"/> for tests. Supports the
/// core Get/Set surface plus OnChange notifications.
/// </summary>
public sealed class TestSettingsApi : IExtensionSettingsApi
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<Action<ExtensionSettingsChange>> _handlers = [];

    public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public T? Get<T>(string key)
    {
        var value = Get(key);
        return value is null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(value));
    }

    public object? GetCore(string path) => Get(path);

    public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        Fire(new ExtensionSettingsChange(key, value, "Fake", "fake"));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        Fire(new ExtensionSettingsChange(key, null, "Fake", "fake"));
        return Task.CompletedTask;
    }

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler) => OnChange(string.Empty, handler);

    public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler)
    {
        lock (_handlers) _handlers.Add(change =>
        {
            if (keyPrefix.Length == 0 || change.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) handler(change);
        });
        return new NullDisposable();
    }

    private void Fire(ExtensionSettingsChange change)
    {
        Action<ExtensionSettingsChange>[] snapshot;
        lock (_handlers) snapshot = _handlers.ToArray();
        foreach (var handler in snapshot) handler(change);
    }
}

/// <summary>
/// A test <see cref="IExtensionApi"/> exposing just the surface the model-roles
/// extension consumes. Concrete instances are reachable via <c>*Impl</c> properties
/// while the interface members are bound to them; unused members throw.
/// </summary>
public sealed class TestExtensionApi : IExtensionApi
{
    private readonly List<(string EventName, ExtensionEventHandler Handler)> _handlers = [];

    public ExtensionDescriptor Descriptor { get; set; } = new("pisharp-model-roles", "PiSharp Model Roles", "0.1.0");
    public string Cwd { get; set; } = "/";
    public bool HasUi { get; set; }
    public IExtensionUi Ui => NoExtensionUi.Instance;

    // Concrete implementations (tests reach through these).
    public TestSettingsApi SettingsImpl { get; } = new();

    public List<ExtensionCommandRegistration> Commands { get; } = [];
    public List<AgentMessage> SentMessages { get; } = [];

    // Interface surface bound to the concrete implementations.
    public IExtensionSettingsApi Settings => SettingsImpl;

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        _handlers.Add((eventName, handler));
        return new NullDisposable();
    }

    /// <summary>Invokes every handler registered for <paramref name="eventName"/>.</summary>
    public async Task RaiseAsync(string eventName, AgentHarnessEvent original, object? payload)
    {
        var evt = new ExtensionEvent(eventName, original, payload);
        foreach (var (name, handler) in _handlers.ToArray())
        {
            if (name == eventName) await handler(evt, CancellationToken.None);
        }
    }

    public IDisposable RegisterCommand(ExtensionCommandRegistration registration)
    {
        Commands.Add(registration);
        return new NullDisposable();
    }

    public Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, ExtensionMessageDelivery.NextTurn, false, cancellationToken);

    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    // Unused by the model-roles extension:
    public IExtensionSessionApi Session => throw new NotSupportedException();
    public IExtensionToolApi Tools => throw new NotSupportedException();
    public IExtensionSkillApi Skills => throw new NotSupportedException();
    public IExtensionModelApi Model => throw new NotSupportedException();
    public IExtensionEventBus Events => throw new NotSupportedException();
    public IExtensionPromptApi Prompt => throw new NotSupportedException();
    public IExtensionStateApi State => throw new NotSupportedException();
    public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException();
    public IDisposable RegisterTool(ExtensionToolRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterSkill(ExtensionSkillDefinition definition) => throw new NotSupportedException();
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException();
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
    public bool RemoveProvider(string api) => throw new NotSupportedException();
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();
}

internal sealed class NullDisposable : IDisposable
{
    public void Dispose() { }
}
