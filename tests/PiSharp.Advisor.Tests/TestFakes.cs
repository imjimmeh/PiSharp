using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.Advisor.Tests;

/// <summary>
/// Minimal in-memory <see cref="IExtensionSettingsApi"/> for tests. Supports the
/// namespaced Get/Set surface plus OnChange notifications.
/// </summary>
public sealed class TestSettingsApi : IExtensionSettingsApi
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<Action<ExtensionSettingsChange>> _handlers = [];

    public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public T? Get<T>(string key)
    {
        if (!_values.TryGetValue(key, out var stored) || stored is null) return default;
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(stored));
    }

    public object? GetCore(string path) => Get(path);

    public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        if (value is null) _values.Remove(key);
        else _values[key] = value;
        Fire(new ExtensionSettingsChange(key, value, "test", "test"));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
        => SetAsync(key, null, scope, cancellationToken);

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
    {
        _handlers.Add(handler);
        return new ActionDisposable(() => _handlers.Remove(handler));
    }

    public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler)
    {
        return OnChange(change =>
        {
            if (change.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) handler(change);
        });
    }

    private void Fire(ExtensionSettingsChange change)
    {
        foreach (var handler in _handlers.ToArray()) handler(change);
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// Configurable in-memory <see cref="IExtensionCompletionApi"/>. Tests set
/// <see cref="CompleteHandler"/> to return a scripted result.
/// </summary>
public sealed class TestCompletionApi : IExtensionCompletionApi
{
    public Func<string, string, IReadOnlyList<AgentMessage>?, string?, ExtensionCompleteRequest?, CancellationToken, Task<ExtensionCompletionResult>> CompleteHandler { get; set; }
        = (_, _, _, _, _, _) => Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Ok, "note", null, null));

    public List<(string Provider, string ModelId, int MessageCount)> Calls { get; } = [];

    public Task<ExtensionCompletionResult> CompleteAsync(
        string provider,
        string modelId,
        IReadOnlyList<AgentMessage>? messages,
        string? systemPrompt = null,
        ExtensionCompleteRequest? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((provider, modelId, messages?.Count ?? 0));
        return CompleteHandler(provider, modelId, messages, systemPrompt, options, cancellationToken);
    }

    public Task<ExtensionCompletionResult> CompleteSimpleAsync(
        string provider, string modelId, string prompt,
        ExtensionCompleteRequest? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ExtensionCompletionDelta> StreamAsync(
        string provider, string modelId,
        IReadOnlyList<AgentMessage>? messages, string? systemPrompt = null,
        ExtensionCompleteRequest? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>A test event bus that records emitted events.</summary>
public sealed class TestEventBus : IExtensionEventBus
{
    public List<(string Name, object? Payload)> Emitted { get; } = [];

    public IDisposable On(string eventName, ExtensionEventHandler handler) => new NullDisposable();

    public Task EmitAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        Emitted.Add((eventName, payload));
        return Task.CompletedTask;
    }
}

/// <summary>A test session API that records appended custom entries.</summary>
public sealed class TestSessionApi : IExtensionSessionApi
{
    public List<(string Type, object? Data)> Appended { get; } = [];

    public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
    {
        Appended.Add((customType, data));
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task SetNameAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ExtensionSessionReplacementResult> NewSessionAsync(Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSessionReplacementResult(true, "unsupported"));
    public Task<ExtensionSessionReplacementResult> ForkAsync(string? entryId = null, string? position = "before", Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSessionReplacementResult(true, "unsupported"));
    public Task<ExtensionSessionReplacementResult> SwitchSessionAsync(string sessionPathOrId, Func<IExtensionReplacementSessionApi, CancellationToken, Task>? withSession = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ExtensionSessionReplacementResult(true, "unsupported"));
    public Task NavigateTreeAsync(string targetId, bool summarize = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> IsIdleAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> HasPendingMessagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// A test <see cref="IExtensionApi"/> exposing just the surface the advisor
/// consumes. Concrete instances are reachable via <c>*Impl</c> properties while
/// the interface members are bound to them; unused members throw.
/// </summary>
public sealed class TestExtensionApi : IExtensionApi
{
    private readonly List<(string EventName, ExtensionEventHandler Handler)> _handlers = [];

    public ExtensionDescriptor Descriptor { get; set; } = new("pisharp-advisor", "PiSharp Advisor", "0.1.0");
    public string Cwd { get; set; } = "/";
    public bool HasUi { get; set; }
    public IExtensionUi Ui => NoExtensionUi.Instance;

    // Concrete implementations (tests reach through these).
    public TestSettingsApi SettingsImpl { get; } = new();
    public TestEventBus EventsImpl { get; } = new();
    public TestCompletionApi CompletionImpl { get; } = new();
    public TestSessionApi SessionImpl { get; } = new();

    public List<ExtensionCommandRegistration> Commands { get; } = [];
    public List<AgentMessage> SentMessages { get; } = [];

    // Interface surface bound to the concrete implementations.
    public IExtensionSettingsApi Settings => SettingsImpl;
    public IExtensionEventBus Events => EventsImpl;
    public IExtensionCompletionApi Completion => CompletionImpl;
    public IExtensionSessionApi Session => SessionImpl;

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

    // Unused by the advisor:
    public IExtensionToolApi Tools => throw new NotSupportedException();
    public IExtensionSkillApi Skills => throw new NotSupportedException();
    public IExtensionModelApi Model => throw new NotSupportedException();
    public IExtensionPromptApi Prompt => throw new NotSupportedException();
    public IExtensionStateApi State => throw new NotSupportedException();
    public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException();
    public IDisposable RegisterTool(ExtensionToolRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException();
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
