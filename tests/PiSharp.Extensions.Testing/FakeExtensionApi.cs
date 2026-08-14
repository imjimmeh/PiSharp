using PiSharp.Abstractions.Messages;
using System.Text.Json;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Testing;

public sealed class FakeExtensionApi : IExtensionApi
{
    private readonly List<ExtensionToolRegistration> _tools = [];
    private readonly List<(string EventName, ExtensionEventHandler Handler)> _handlers = [];
    private readonly List<ExtensionMiddleware> _middlewares = [];
    private readonly List<CapturedMessage> _sentMessages = [];
    private readonly List<IFileContentExtractor> _contentExtractors = [];
    private readonly List<ISearchProvider> _searchProviders = [];

    public string Cwd { get; set; } = "/";
    public bool HasUi { get; set; }
    public IExtensionUi Ui { get; set; } = NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; set; } = FakeExtensionDescriptor.Default;

    public IReadOnlyList<ExtensionToolRegistration> RegisteredTools => _tools;
    public IReadOnlyList<(string EventName, ExtensionEventHandler Handler)> RegisteredHandlers => _handlers;
    public IReadOnlyList<ExtensionMiddleware> RegisteredMiddlewares => _middlewares;
    public IReadOnlyList<CapturedMessage> SentMessages => _sentMessages;

    // Captured registrations
    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        _handlers.Add((eventName, handler));
        return NullDisposable.Instance;
    }

    public IDisposable Use(ExtensionMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return NullDisposable.Instance;
    }

    public IDisposable RegisterTool(ExtensionToolRegistration registration)
    {
        _tools.Add(registration);
        return NullDisposable.Instance;
    }

    public Task SendMessageAsync(
        AgentMessage message,
        ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp,
        bool triggerTurn = false,
        CancellationToken cancellationToken = default)
    {
        _sentMessages.Add(new CapturedMessage(message, delivery, triggerTurn, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    // Sub-APIs — not supported; use the captured lists above
    public IExtensionSessionApi Session =>
        throw new NotSupportedException("FakeExtensionApi.Session is not supported. Use SentMessages to inspect sent messages.");
    public IExtensionToolApi Tools =>
        throw new NotSupportedException("FakeExtensionApi.Tools is not supported. Use RegisteredTools to inspect tool registrations.");
    public IExtensionSkillApi Skills =>
        throw new NotSupportedException("FakeExtensionApi.Skills is not supported.");
    public IExtensionModelApi Model =>
        throw new NotSupportedException("FakeExtensionApi.Model is not supported.");
    public IExtensionEventBus Events =>
        throw new NotSupportedException("FakeExtensionApi.Events is not supported. Use RegisteredHandlers to inspect event registrations.");
    public IExtensionPromptApi Prompt =>
        throw new NotSupportedException("FakeExtensionApi.Prompt is not supported.");
    public IExtensionSettingsApi Settings { get; set; } = new InMemorySettingsApi();
    public IExtensionStateApi State { get; set; } = new InMemoryStateApi();
    public IExtensionFileApi Files => new CapturedFileApi(this);
    public IExtensionSearchApi Search => new CapturedSearchApi(this);

    public IReadOnlyList<IFileContentExtractor> RegisteredContentExtractors => _contentExtractors;
    public IReadOnlyList<ISearchProvider> RegisteredSearchProviders => _searchProviders;

    private sealed class CapturedFileApi(FakeExtensionApi api) : IExtensionFileApi
    {
        public IDisposable RegisterContentExtractor(IFileContentExtractor extractor, bool overrideExisting = false)
        {
            if (!overrideExisting && api._contentExtractors.Any(e => string.Equals(e.Id, extractor.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Content extractor '{extractor.Id}' is already registered.");
            api._contentExtractors.RemoveAll(e => string.Equals(e.Id, extractor.Id, StringComparison.OrdinalIgnoreCase));
            api._contentExtractors.Add(extractor);
            return new NullDisposable();
        }
        public IReadOnlyList<IFileContentExtractor> ContentExtractors => api._contentExtractors;
    }

    private sealed class CapturedSearchApi(FakeExtensionApi api) : IExtensionSearchApi
    {
        public IDisposable RegisterProvider(ISearchProvider provider, bool overrideExisting = false)
        {
            if (!overrideExisting && api._searchProviders.Any(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Search provider '{provider.Id}' is already registered.");
            api._searchProviders.RemoveAll(p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            api._searchProviders.Add(provider);
            return new NullDisposable();
        }
        public IReadOnlyList<ISearchProvider> Providers => api._searchProviders;
        public ISearchProvider? GetProvider(string providerId) => api._searchProviders.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    // Remaining IExtensionApi members — throw until promoted if a test needs them
    public IDisposable RegisterSkill(ExtensionSkillDefinition r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterSkill is not supported.");
    public IDisposable RegisterCommand(ExtensionCommandRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterCommand is not supported.");
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterShortcut is not supported.");
    public IDisposable RegisterFlag(ExtensionFlagRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterFlag is not supported.");
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterMessageRenderer is not supported.");
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration r) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterMessageDecorator is not supported.");
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) =>
        throw new NotSupportedException("FakeExtensionApi.RegisterProvider is not supported.");
    public bool RemoveProvider(string api) =>
        throw new NotSupportedException("FakeExtensionApi.RemoveProvider is not supported.");
    public object? GetFlag(string name) => null;
    public IReadOnlyDictionary<string, object?> GetFlags() => new Dictionary<string, object?>();

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }


    private sealed class InMemorySettingsApi : IExtensionSettingsApi
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
            Fire(new ExtensionSettingsChange(key, value, "Fake", "fake"));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            Fire(new ExtensionSettingsChange(key, null, "Fake", "fake"));
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

    private sealed class InMemoryStateApi : IExtensionStateApi
    {
        private readonly Dictionary<(string Key, ExtensionStateScope Scope), object?> _values = [];
        private readonly Dictionary<ExtensionStateScope, int> _versions = [];

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

        public Task<int> GetSchemaVersionAsync(ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
            => Task.FromResult(_versions.TryGetValue(scope, out var version) ? version : 0);

        public Task<int> SetSchemaVersionAsync(int version, ExtensionStateScope scope = ExtensionStateScope.User, CancellationToken cancellationToken = default)
        {
            _versions[scope] = version;
            return Task.FromResult(version);
        }

        public Task RegisterMigrationAsync(
            int fromVersion,
            int toVersion,
            Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> migrate,
            ExtensionStateScope scope = ExtensionStateScope.User,
            CancellationToken cancellationToken = default)
        {
            if (toVersion <= fromVersion) throw new ArgumentException("Migration toVersion must be greater than fromVersion.", nameof(toVersion));
            return Task.CompletedTask;
        }
    }

    private sealed class ChangeSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
