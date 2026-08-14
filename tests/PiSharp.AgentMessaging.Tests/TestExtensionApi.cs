using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

// Deliberately a SIBLING namespace of PiSharp.AgentMessaging: types declared in an
// enclosing namespace (PiSharp.AgentMessaging.AgentMessage) would shadow the
// abstractions AgentMessage used by the IExtensionApi surface.
namespace PiSharp.Tests.AgentMessaging;

/// <summary>
/// In-memory <see cref="IExtensionApi"/> exposing exactly the surface the
/// agent-messaging extension consumes: tool/skill registration, event
/// subscription + raising, harness message injection, wire event emission,
/// session naming, and settings.
/// </summary>
public sealed class TestExtensionApi : IExtensionApi
{
    private readonly List<(string EventName, ExtensionEventHandler Handler)> _handlers = [];

    public ExtensionDescriptor Descriptor { get; set; } = new("agent-messaging", "PiSharp Agent Messaging", "0.1.0", SourceId: "pi:extension:agent-messaging");
    public string Cwd { get; set; } = "/";
    public bool HasUi { get; set; }
    public IExtensionUi Ui => NoExtensionUi.Instance;
    public string SessionName { get; set; } = "root-session";

    public TestSettingsApi SettingsImpl { get; } = new();
    public List<ExtensionToolRegistration> RegisteredTools { get; } = [];
    public List<ExtensionSkillDefinition> RegisteredSkills { get; } = [];
    public List<CapturedMessage> SentMessages { get; } = [];
    public List<(string EventName, object? Payload)> EmittedClientEvents { get; } = [];

    public IExtensionSettingsApi Settings => SettingsImpl;

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        _handlers.Add((eventName, handler));
        return new NullDisposable();
    }

    public IReadOnlyList<(string EventName, ExtensionEventHandler Handler)> Handlers => _handlers.ToArray();

    /// <summary>Raises an event to every handler registered for the name and returns the event.</summary>
    public async Task<ExtensionEvent> RaiseAsync(string eventName, object? payload = null)
    {
        var evt = new ExtensionEvent(eventName, new AgentHarnessEvent.Core(new AgentEvent.AgentStart()), payload);
        foreach (var (name, handler) in _handlers.ToArray())
        {
            if (name == eventName)
                await handler(evt, CancellationToken.None);
        }
        return evt;
    }
    public IDisposable RegisterTool(ExtensionToolRegistration registration)
    {
        RegisteredTools.Add(registration);
        return new NullDisposable();
    }

    public IDisposable RegisterSkill(ExtensionSkillDefinition registration)
    {
        RegisteredSkills.Add(registration);
        return new NullDisposable();
    }

    public Task SendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default)
        => SendMessageAsync(message, ExtensionMessageDelivery.NextTurn, false, cancellationToken);

    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(new CapturedMessage(message, delivery, triggerTurn, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task EmitClientEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
    {
        EmittedClientEvents.Add((eventName, payload));
        return Task.CompletedTask;
    }

    public IExtensionSessionApi Session => new TestSessionApi(this);

    // --- Unused surface ---
    public IExtensionToolApi Tools => throw new NotSupportedException();
    public IExtensionSkillApi Skills => throw new NotSupportedException();
    public IExtensionModelApi Model => throw new NotSupportedException();
    public IExtensionEventBus Events => throw new NotSupportedException();
    public IExtensionPromptApi Prompt => throw new NotSupportedException();
    public IExtensionStateApi State => throw new NotSupportedException();
    public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException();
    public IDisposable RegisterCommand(ExtensionCommandRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException();
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
    public bool RemoveProvider(string api) => throw new NotSupportedException();
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();

    private sealed class TestSessionApi(TestExtensionApi api) : IExtensionSessionApi
    {
        public Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(api.SessionName);

        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => api.SendMessageAsync(message, delivery, triggerTurn, cancellationToken);

        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
            => api.SendMessageAsync(PiSharp.Abstractions.Messages.AgentMessages.User(content), delivery, false, cancellationToken);

        public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetNameAsync(string name, CancellationToken cancellationToken = default)
        {
            api.SessionName = name;
            return Task.CompletedTask;
        }

        public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

/// <summary>Captured harness message injection.</summary>
public sealed record CapturedMessage(
    AgentMessage Message,
    ExtensionMessageDelivery Delivery,
    bool TriggerTurn,
    DateTimeOffset Timestamp);

/// <summary>In-memory settings surface with JSON round-tripping like the shared fake.</summary>
public sealed class TestSettingsApi : IExtensionSettingsApi
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public T? Get<T>(string key)
    {
        var value = Get(key);
        return value is null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    }

    public object? GetCore(string path) => Get(path);

    public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }

    public IDisposable OnChange(Action<ExtensionSettingsChange> handler) => new NullDisposable();
    public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler) => new NullDisposable();
}

internal sealed class NullDisposable : IDisposable
{
    public static readonly NullDisposable Instance = new();
    public void Dispose() { }
}
