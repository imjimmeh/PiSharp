using System.Text.Json;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.Permissions.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExtensionApi"/> exposing exactly the surface the permissions plugin
/// consumes, following the PiSharp.PlanMode.Tests <c>PlanModeTestApi</c> convention:
/// concrete captured lists reachable from tests, unused members throw.
/// </summary>
public sealed class PermissionsTestApi : IExtensionApi
{
    public sealed record CapturedAuditEntry(string CustomType, object? Data);
    public sealed record CapturedClientEvent(string Name, object? Payload);
    public sealed record CapturedApprovalRequest(string Kind, JsonElement Payload);

    public List<ExtensionMiddleware> RegisteredMiddlewares { get; } = [];
    public List<ExtensionCommandRegistration> RegisteredCommands { get; } = [];
    public List<(string EventName, ExtensionEventHandler Handler)> RegisteredHandlers { get; } = [];
    public List<CapturedClientEvent> ClientEvents { get; } = [];
    public List<CapturedAuditEntry> AuditEntries { get; } = [];
    public List<AgentMessage> SentMessages { get; } = [];
    public List<CapturedApprovalRequest> ApprovalRequests { get; } = [];
    public int DisposedSubscriptions { get; private set; }

    public string Cwd { get; set; } = "C:/project";
    public bool HasUi { get; set; }
    public IExtensionUi Ui { get; set; } = NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; set; } = new("pisharp-permissions", "PiSharp Permissions", "0.1.0");
    public IExecutionEnv? ExecutionEnv { get; set; }
    public string? SessionName { get; set; } = "session-abcdefgh";

    public IExtensionSettingsApi Settings { get; set; } = new InMemorySettingsApi();
    public IExtensionStateApi State { get; set; } = new InMemoryStateApi();
    public IExtensionSessionApi Session => new SessionApi(this);

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        RegisteredHandlers.Add((eventName, handler));
        return TrackDisposable();
    }

    public IDisposable Use(ExtensionMiddleware middleware)
    {
        RegisteredMiddlewares.Add(middleware);
        return TrackDisposable();
    }

    public IDisposable RegisterCommand(ExtensionCommandRegistration registration)
    {
        RegisteredCommands.Add(registration);
        return TrackDisposable();
    }

    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.NextTurn, bool triggerTurn = false, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task EmitClientEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
    {
        ClientEvents.Add(new CapturedClientEvent(eventName, payload));
        return Task.CompletedTask;
    }

    /// <summary>Invokes every handler registered for <paramref name="eventName"/>.</summary>
    public async Task RaiseAsync(string eventName, object? payload = null)
    {
        var evt = new ExtensionEvent(eventName, null!, payload);
        foreach (var (name, handler) in RegisteredHandlers.ToArray())
        {
            if (name == eventName) await handler(evt, CancellationToken.None);
        }
    }

    // Unused IExtensionApi surface.
    public IExtensionToolApi Tools => throw new NotSupportedException("PermissionsTestApi.Tools is not supported.");
    public IExtensionSkillApi Skills => throw new NotSupportedException("PermissionsTestApi.Skills is not supported.");
    public IExtensionModelApi Model => throw new NotSupportedException("PermissionsTestApi.Model is not supported.");
    public IExtensionEventBus Events => throw new NotSupportedException("PermissionsTestApi.Events is not supported.");
    public IExtensionPromptApi Prompt => throw new NotSupportedException("PermissionsTestApi.Prompt is not supported.");
    public IDisposable RegisterTool(ExtensionToolRegistration registration) => throw new NotSupportedException("PermissionsTestApi.RegisterTool is not supported.");
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException("PermissionsTestApi.RegisterSkill is not supported.");
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException("PermissionsTestApi.RegisterShortcut is not supported.");
    public IDisposable RegisterFlag(ExtensionFlagRegistration registration) => throw new NotSupportedException("PermissionsTestApi.RegisterFlag is not supported.");
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException("PermissionsTestApi.RegisterMessageRenderer is not supported.");
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException("PermissionsTestApi.RegisterMessageDecorator is not supported.");
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException("PermissionsTestApi.RegisterProvider is not supported.");
    public bool RemoveProvider(string api) => throw new NotSupportedException("PermissionsTestApi.RemoveProvider is not supported.");
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();

    private IDisposable TrackDisposable() => new TrackingDisposable(this);

    private void OnSubscriptionDisposed() => DisposedSubscriptions++;

    private sealed class TrackingDisposable(PermissionsTestApi api) : IDisposable
    {
        public void Dispose() => api.OnSubscriptionDisposed();
    }

    private sealed class SessionApi(PermissionsTestApi api) : IExtensionSessionApi
    {
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => api.SendMessageAsync(message, delivery, triggerTurn, cancellationToken);
        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
            => api.SendMessageAsync(AgentMessages.User(content), delivery, false, cancellationToken);
        public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
        {
            api.AuditEntries.Add(new CapturedAuditEntry(customType, data));
            return Task.CompletedTask;
        }
        public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => Task.FromResult(api.SessionName);
        public Task SetNameAsync(string name, CancellationToken cancellationToken = default) { api.SessionName = name; return Task.CompletedTask; }
        public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Namespaced settings fake that fires OnChange subscribers (P02 shape).</summary>
    public sealed class InMemorySettingsApi : IExtensionSettingsApi
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        private readonly List<Action<ExtensionSettingsChange>> _handlers = [];

        public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public T? Get<T>(string key)
        {
            var value = Get(key);
            return value is null ? default : JsonSerializer.Deserialize<T>(ExtensionSettingKeys.ToJsonNode(value)!.ToJsonString());
        }

        public object? GetCore(string path) => Get(path);

        public Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            Fire(new ExtensionSettingsChange($"extensions.pisharp-permissions.{key}", value, "Fake", "fake"));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            Fire(new ExtensionSettingsChange($"extensions.pisharp-permissions.{key}", null, "Fake", "fake"));
            return Task.CompletedTask;
        }

        public IDisposable OnChange(Action<ExtensionSettingsChange> handler)
        {
            lock (_handlers) _handlers.Add(handler);
            return new ChangeSubscription(() => { lock (_handlers) _handlers.Remove(handler); });
        }

        public IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler)
        {
            lock (_handlers) _handlers.Add(change =>
            {
                if (change.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) handler(change);
            });
            return new ChangeSubscription(() => { });
        }

        private void Fire(ExtensionSettingsChange change)
        {
            Action<ExtensionSettingsChange>[] snapshot;
            lock (_handlers) snapshot = _handlers.ToArray();
            foreach (var handler in snapshot) handler(change);
        }
    }

    /// <summary>In-memory state fake with GetAll/ListKeys (P02 State shape).</summary>
    public sealed class InMemoryStateApi : IExtensionStateApi
    {
        private readonly Dictionary<(string Key, ExtensionStateScope Scope), object?> _values = [];

        public Task<object?> GetAsync(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue((key, scope), out var value) ? value : null);

        public Task<T?> GetAsync<T>(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        {
            var value = _values.TryGetValue((key, scope), out var stored) ? stored : null;
            return Task.FromResult(value is null ? default : JsonSerializer.Deserialize<T>(ExtensionSettingKeys.ToJsonNode(value)!.ToJsonString()));
        }

        public Task SetAsync(string key, object? value, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        {
            if (value is null) _values.Remove((key, scope));
            else _values[(key, scope)] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        {
            _values.Remove((key, scope));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, object?>> GetAllAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, object?>>(_values.Where(pair => pair.Key.Scope == scope).ToDictionary(pair => pair.Key.Key, pair => pair.Value, StringComparer.Ordinal));

        public Task<IReadOnlyList<string>> ListKeysAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Where(pair => pair.Key.Scope == scope).Select(pair => pair.Key.Key).ToArray());

        public Task ClearAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        {
            var keys = _values.Keys.Where(key => key.Scope == scope).ToArray();
            foreach (var key in keys) _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<int> GetSchemaVersionAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> SetSchemaVersionAsync(int version, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default) => Task.FromResult(version);
        public Task RegisterMigrationAsync(int fromVersion, int toVersion, Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> migrate, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ChangeSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
