using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Providers;

namespace PiSharp.Extensions;

public sealed record OwnedExtensionRegistration<T>(string Id, string SourceId, T Value, ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject);
public enum ExtensionRegistryChangeKind { Added, Removed, Replaced, Restored, SourceRemoved }
public sealed record ExtensionRegistryChange(ExtensionRegistryChangeKind Kind, string SourceId, string Category, string Key, object? Value = null);

public sealed class ExtensionRegistry
{
    private readonly IExtensionRegistryChangeStream _changeStream;
    private readonly ILogger _logger;
    private readonly object _changedEventGate = new();
    private readonly List<(Func<ExtensionRegistryChange, CancellationToken, Task> Handler, IDisposable Subscription)> _changedEventSubscriptions = [];

    public ExtensionRegistry(IExtensionRegistryChangeStream? changeStream = null, ILoggerFactory? loggerFactory = null)
    {
        _changeStream = changeStream ?? new ExtensionRegistryChangeStream(loggerFactory);
        _logger = loggerFactory?.CreateLogger<ExtensionRegistry>() ?? NullLogger<ExtensionRegistry>.Instance;
    }

    public event Func<ExtensionRegistryChange, CancellationToken, Task> Changed
    {
        add
        {
            if (value is null) return;
            lock (_changedEventGate) _changedEventSubscriptions.Add((value, _changeStream.Subscribe(value)));
        }
        remove
        {
            if (value is null) return;
            lock (_changedEventGate)
            {
                for (var index = _changedEventSubscriptions.Count - 1; index >= 0; index--)
                {
                    var (handler, subscription) = _changedEventSubscriptions[index];
                    if (!handler.Equals(value)) continue;
                    _changedEventSubscriptions.RemoveAt(index);
                    subscription.Dispose();
                    return;
                }
            }
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, RegistrationStack<IAgentTool>> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<ExtensionSkillDefinition>> _skills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<IModelProvider>> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<(string EventName, ExtensionEventHandler Handler)>> _handlers = new(StringComparer.Ordinal);
    private readonly List<string> _handlerRegistrationOrder = [];
    private readonly Dictionary<string, OwnedExtensionRegistration<ExtensionMiddleware>> _middleware = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<ExtensionCommandRegistration>> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<ExtensionShortcutRegistration>> _shortcuts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<ExtensionFlagRegistration>> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<ExtensionMessageRendererRegistration>> _renderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<ExtensionMessageDecoratorRegistration>> _decorators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<IPromptContributor>> _promptContributors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<PromptSection>> _promptSections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<IPromptTransform>> _promptTransforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<IRuleProvider>> _ruleProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedExtensionRegistration<IStreamDeltaInterceptor>> _streamDeltaInterceptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<ISkillProvider>> _skillProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationStack<ExtensionSkillRunner>> _skillRunners = new(StringComparer.Ordinal);

    public IReadOnlySet<string> BuiltInToolNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<OwnedExtensionRegistration<IAgentTool>> Tools => SnapshotWinners(_tools);
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionSkillDefinition>> Skills => SnapshotWinners(_skills);
    public IReadOnlyList<OwnedExtensionRegistration<ISkillProvider>> SkillProviders => SnapshotWinners(_skillProviders);
    public IReadOnlyList<OwnedExtensionRegistration<IModelProvider>> Providers => SnapshotWinners(_providers);
    public IReadOnlyList<OwnedExtensionRegistration<(string EventName, ExtensionEventHandler Handler)>> Handlers => Snapshot(_handlers, _handlerRegistrationOrder);
    public IReadOnlyList<OwnedExtensionRegistration<(string EventName, ExtensionEventHandler Handler)>> HandlersFor(string eventName)
        => Handlers.Where(handler => StringComparer.Ordinal.Equals(handler.Value.EventName, eventName)).ToArray();
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionMiddleware>> Middleware => Snapshot(_middleware);
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionCommandRegistration>> Commands => Snapshot(_commands);
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>> Shortcuts => Snapshot(_shortcuts);
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionFlagRegistration>> Flags => Snapshot(_flags);
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionMessageRendererRegistration>> Renderers => SnapshotWinners(_renderers);
    public IReadOnlyList<OwnedExtensionRegistration<ExtensionMessageDecoratorRegistration>> Decorators => Snapshot(_decorators);

    public OwnedExtensionRegistration<ExtensionMessageRendererRegistration>? FindRendererByCustomType(string customType)
    {
        var key = RendererKeyByCustomType(customType);
        lock (_gate)
        {
            return _renderers.TryGetValue(key, out var stack) ? stack.Current : null;
        }
    }
    public IReadOnlyList<OwnedExtensionRegistration<IPromptContributor>> PromptContributors => Snapshot(_promptContributors);
    public IReadOnlyList<OwnedExtensionRegistration<PromptSection>> PromptSections => SnapshotWinners(_promptSections);
    public IReadOnlyList<OwnedExtensionRegistration<IPromptTransform>> PromptTransforms => Snapshot(_promptTransforms);
    public IReadOnlyList<OwnedExtensionRegistration<IRuleProvider>> RuleProviders => SnapshotWinners(_ruleProviders);
    public IReadOnlyList<OwnedExtensionRegistration<IStreamDeltaInterceptor>> StreamDeltaInterceptors => Snapshot(_streamDeltaInterceptors);
    public IReadOnlyList<string> SourceIds => AllSourceIds().Distinct(StringComparer.Ordinal).ToArray();

    public IDisposable RegisterTool(string sourceId, IAgentTool tool, ExtensionOverridePolicy overridePolicy = ExtensionOverridePolicy.Reject)
    {
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Tool name is required.", nameof(tool));
        if (BuiltInToolNames.Contains(tool.Name) && overridePolicy != ExtensionOverridePolicy.OverrideBuiltIn)
            throw new InvalidOperationException($"tool '{tool.Name}' is built in and requires ExtensionOverridePolicy.OverrideBuiltIn.");
        return Push(_tools, $"tool:{tool.Name}", sourceId, tool, "tool", overridePolicy);
    }

    public IDisposable RegisterProvider(string sourceId, IModelProvider provider, ExtensionOverridePolicy overridePolicy = ExtensionOverridePolicy.Reject)
    {
        if (string.IsNullOrWhiteSpace(provider.Api)) throw new ArgumentException("Provider API is required.", nameof(provider));
        return Push(_providers, $"provider:{provider.Api}", sourceId, provider, "provider", overridePolicy);
    }

    public IDisposable RegisterSkill(string sourceId, ExtensionSkillDefinition registration, ExtensionOverridePolicy overridePolicy = ExtensionOverridePolicy.Reject)
    {
        if (string.IsNullOrWhiteSpace(registration.Name)) throw new ArgumentException("Skill name is required.", nameof(registration));
        if (string.IsNullOrWhiteSpace(registration.Description)) throw new ArgumentException("Skill description is required.", nameof(registration));
        if (string.IsNullOrWhiteSpace(registration.FilePath)) throw new ArgumentException("Skill file path is required.", nameof(registration));
        var skillHandle = Push(_skills, $"skill:{registration.Name}", sourceId, registration, "skill", overridePolicy);
        if (registration.Runner is null) return skillHandle;
        var runnerHandle = Push(_skillRunners, $"skill-runner:{registration.Name}", sourceId, registration.Runner, "skill-runner", overridePolicy);
        return new CompositeHandle(skillHandle, runnerHandle);
    }

    /// <summary>
    /// Registers a skill provider whose discovered skills merge with
    /// first-wins dedup by name (higher <see cref="ExtensionSkillDefinition.SourcePriority"/> wins).
    /// </summary>
    public IDisposable RegisterSkillProvider(string sourceId, ISkillProvider provider, ExtensionOverridePolicy overridePolicy = ExtensionOverridePolicy.Reject)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)) throw new ArgumentException("Skill provider name is required.", nameof(provider));
        return Push(_skillProviders, $"skill-provider:{provider.Name}", sourceId, provider, "skill-provider", overridePolicy);
    }

    /// <summary>Returns the runner of the current winning registration for the named skill, if any.</summary>
    public ExtensionSkillRunner? GetSkillRunner(string name)
    {
        lock (_gate)
        {
            return _skillRunners.TryGetValue($"skill-runner:{name}", out var stack)
                ? stack.Current?.Value
                : null;
        }
    }

    /// <summary>
    /// Merges registered extension skills with all registered skill providers'
    /// discovered skills. Dedup by name is first-wins with higher
    /// <c>SourcePriority</c> winning; ties keep the already-registered skill.
    /// </summary>
    public async Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverSkillProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = SkillProviders;
        var discovered = new List<ExtensionSkillDefinition>();
        foreach (var provider in providers)
        {
            var skills = await provider.Value.DiscoverAsync(cancellationToken);
            foreach (var skill in skills)
            {
                discovered.Add(skill with
                {
                    Source = string.IsNullOrWhiteSpace(skill.Source) ? provider.Value.Name : skill.Source,
                    SourcePriority = skill.SourcePriority != 0 ? skill.SourcePriority : provider.Value.Priority
                });
            }
        }

        var byName = new Dictionary<string, ExtensionSkillDefinition>(StringComparer.Ordinal);
        foreach (var registration in Skills) byName[registration.Value.Name] = registration.Value;
        foreach (var skill in discovered)
        {
            if (!byName.TryGetValue(skill.Name, out var existing) || skill.SourcePriority > existing.SourcePriority)
                byName[skill.Name] = skill;
        }
        return byName.Values.ToArray();
    }

    public IDisposable RegisterHandler(string sourceId, string eventName, ExtensionEventHandler handler)
    {
        if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("Event name is required.", nameof(eventName));
        var key = $"handler:{eventName}:{Guid.NewGuid():N}";
        Set(_handlers, key, sourceId, (eventName, handler), "handler");
        lock (_gate) _handlerRegistrationOrder.Add(key);
        return new RegistryHandle(() =>
        {
            Remove(_handlers, key, "handler");
            lock (_gate) _handlerRegistrationOrder.Remove(key);
        });
    }

    public IDisposable RegisterMiddleware(string sourceId, ExtensionMiddleware middleware)
        => SetOwned(_middleware, $"middleware:{Guid.NewGuid():N}", sourceId, middleware, "middleware");

    public IDisposable RegisterCommand(string sourceId, ExtensionCommandRegistration registration)
        => SetOwned(_commands, $"command:{registration.Name}:{sourceId}:{Guid.NewGuid():N}", sourceId, registration, "command");

    public IDisposable RegisterShortcut(string sourceId, ExtensionShortcutRegistration registration)
        => SetOwned(_shortcuts, $"shortcut:{registration.Keys}:{sourceId}", sourceId, registration, "shortcut");

    public IDisposable RegisterFlag(string sourceId, ExtensionFlagRegistration registration)
        => SetOwned(_flags, $"flag:{registration.Name}", sourceId, registration, "flag");

    public IDisposable RegisterMessageRenderer(string sourceId, ExtensionMessageRendererRegistration registration)
    {
        var key = RendererKey(registration);
        return registration.Handler is null
            ? SetOwnedStack(_renderers, key, sourceId, registration, "renderer", registration.Override)
            : Push(_renderers, key, sourceId, registration, "renderer", registration.Override);
    }

    public IDisposable RegisterMessageDecorator(string sourceId, ExtensionMessageDecoratorRegistration registration)
    {
        var key = $"decorator:{registration.RowType}:{registration.Order:D10}:{registration.Name}:{sourceId}:{Guid.NewGuid():N}";
        return SetOwned(_decorators, key, sourceId, registration, "decorator");
    }

    public IDisposable RegisterPromptContributor(string sourceId, IPromptContributor contributor)
        => SetOwned(_promptContributors, $"prompt-contributor:{Guid.NewGuid():N}", sourceId, contributor, "prompt-contributor");

    public IDisposable RegisterPromptSection(string sourceId, PromptSection section, ExtensionOverridePolicy overridePolicy = ExtensionOverridePolicy.Reject)
    {
        if (string.IsNullOrWhiteSpace(section.Id)) throw new ArgumentException("Prompt section id is required.", nameof(section));
        return Push(_promptSections, $"prompt-section:{section.Id}", sourceId, section, "prompt-section", overridePolicy);
    }

    public IDisposable RegisterPromptTransform(string sourceId, IPromptTransform transform)
        => SetOwned(_promptTransforms, $"prompt-transform:{Guid.NewGuid():N}", sourceId, transform, "prompt-transform");

    public IDisposable RegisterRuleProvider(string sourceId, IRuleProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)) throw new ArgumentException("Rule provider name is required.", nameof(provider));
        var key = $"rule-provider:{provider.Name}";
        lock (_gate)
        {
            if (_ruleProviders.TryGetValue(key, out var stack) && stack.Current is not null)
            {
                _logger.LogWarning("Rule provider '{Name}' is already registered by '{ExistingSource}'; replacing it with '{NewSource}'.", provider.Name, stack.Current.SourceId, sourceId);
            }
        }
        return Push(_ruleProviders, key, sourceId, provider, "rule-provider", ExtensionOverridePolicy.Override);
    }

    public IDisposable RegisterStreamDeltaInterceptor(string sourceId, IStreamDeltaInterceptor interceptor)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
        return SetOwned(_streamDeltaInterceptors, $"stream-delta:{sourceId}", sourceId, interceptor, "stream-delta");
    }

    public int UnregisterBySource(string sourceId)
    {
        var changes = new List<ExtensionRegistryChange>();
        int removed;
        lock (_gate)
        {
            removed = RemoveBySourceStack(_tools, sourceId, "tool", changes)
                + RemoveBySourceStack(_skills, sourceId, "skill", changes)
                + RemoveBySourceStack(_providers, sourceId, "provider", changes)
                + RemoveBySource(_handlers, sourceId)
                + RemoveBySource(_middleware, sourceId)
                + RemoveBySource(_commands, sourceId)
                + RemoveBySource(_shortcuts, sourceId)
                + RemoveBySource(_flags, sourceId)
                + RemoveBySourceStack(_renderers, sourceId, "renderer", changes)
                + RemoveBySource(_decorators, sourceId)
                + RemoveBySource(_promptContributors, sourceId)
                + RemoveBySourceStack(_promptSections, sourceId, "prompt-section", changes)
                + RemoveBySource(_promptTransforms, sourceId)
                + RemoveBySourceStack(_ruleProviders, sourceId, "rule-provider", changes)
                + RemoveBySource(_streamDeltaInterceptors, sourceId)
                + RemoveBySourceStack(_skillProviders, sourceId, "skill-provider", changes)
                + RemoveBySourceStack(_skillRunners, sourceId, "skill-runner", changes);
        }
        foreach (var change in changes) Publish(change);
        if (removed > 0) Publish(new ExtensionRegistryChange(ExtensionRegistryChangeKind.SourceRemoved, sourceId, "source", sourceId, removed));
        return removed;
    }

    public async Task DispatchAsync(AgentHarnessEvent evt, CancellationToken cancellationToken = default)
    {
        var mapped = ExtensionEventMapper.Map(evt);
        foreach (var registration in Handlers.Where(handler => StringComparer.Ordinal.Equals(handler.Value.EventName, mapped.Name)))
        {
            await registration.Value.Handler(mapped, cancellationToken);
        }
    }

    private IEnumerable<string> AllSourceIds()
    {
        lock (_gate)
        {
            return AllStackRegistrations(_tools).Select(x => x.SourceId)
                .Concat(AllStackRegistrations(_skills).Select(x => x.SourceId))
                .Concat(AllStackRegistrations(_providers).Select(x => x.SourceId))
                .Concat(_handlers.Values.Select(x => x.SourceId))
                .Concat(_middleware.Values.Select(x => x.SourceId))
                .Concat(_commands.Values.Select(x => x.SourceId))
                .Concat(_shortcuts.Values.Select(x => x.SourceId))
                .Concat(_flags.Values.Select(x => x.SourceId))
                .Concat(AllStackRegistrations(_renderers).Select(x => x.SourceId))
                .Concat(_decorators.Values.Select(x => x.SourceId))
                .Concat(_promptContributors.Values.Select(x => x.SourceId))
                .Concat(AllStackRegistrations(_promptSections).Select(x => x.SourceId))
                .Concat(_promptTransforms.Values.Select(x => x.SourceId))
                .Concat(AllStackRegistrations(_ruleProviders).Select(x => x.SourceId))
                .Concat(_streamDeltaInterceptors.Values.Select(x => x.SourceId))
                .Concat(AllStackRegistrations(_skillProviders).Select(x => x.SourceId))
                .Concat(AllStackRegistrations(_skillRunners).Select(x => x.SourceId))
                .ToArray();
        }
    }

    private IReadOnlyList<OwnedExtensionRegistration<T>> Snapshot<T>(Dictionary<string, OwnedExtensionRegistration<T>> source, IReadOnlyList<string>? order = null)
    {
        lock (_gate)
        {
            if (order is null) return source.Values.ToArray();
            return order.Where(source.ContainsKey).Select(key => source[key]).ToArray();
        }
    }

    private IReadOnlyList<OwnedExtensionRegistration<T>> SnapshotWinners<T>(Dictionary<string, RegistrationStack<T>> source)
    {
        lock (_gate) return source.Values.Select(stack => stack.Current).OfType<OwnedExtensionRegistration<T>>().ToArray();
    }

    private static IEnumerable<OwnedExtensionRegistration<T>> AllStackRegistrations<T>(Dictionary<string, RegistrationStack<T>> source)
        => source.Values.SelectMany(stack => stack.Items);

    private void Publish(ExtensionRegistryChange change)
        => _changeStream.PublishAsync(change, CancellationToken.None).GetAwaiter().GetResult();

    private void Set<T>(Dictionary<string, OwnedExtensionRegistration<T>> target, string key, string sourceId, T value, string category)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
        lock (_gate) target[key] = new OwnedExtensionRegistration<T>(key, sourceId, value);
        Publish(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Added, sourceId, category, key, value));
    }

    private IDisposable SetOwned<T>(Dictionary<string, OwnedExtensionRegistration<T>> target, string key, string sourceId, T value, string category)
    {
        Set(target, key, sourceId, value, category);
        return new RegistryHandle(() => Remove(target, key, category));
    }

    private IDisposable SetOwnedStack<T>(Dictionary<string, RegistrationStack<T>> target, string key, string sourceId, T value, string category, ExtensionOverridePolicy overridePolicy)
    {
        OwnedExtensionRegistration<T> current;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
            if (!target.TryGetValue(key, out var stack)) target[key] = stack = new RegistrationStack<T>();
            current = new OwnedExtensionRegistration<T>(key, sourceId, value, overridePolicy);
            stack.Push(current);
        }
        Publish(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Added, sourceId, category, key, value));
        return new RegistryHandle(() => RemoveStackEntry(target, key, category, current));
    }

    private static string RendererKey(ExtensionMessageRendererRegistration registration)
        => !string.IsNullOrWhiteSpace(registration.CustomType)
            ? RendererKeyByCustomType(registration.CustomType!)
            : registration.RowType == ExtensionChatRowType.Unknown
            ? $"renderer:name:{registration.Name}"
            : $"renderer:row:{registration.RowType}";

    private static string RendererKeyByName(string name)
        => $"renderer:name:{name}";

    private static string RendererKeyByCustomType(string customType)
        => $"renderer:custom:{customType}";

    private IDisposable Push<T>(Dictionary<string, RegistrationStack<T>> target, string key, string sourceId, T value, string category, ExtensionOverridePolicy overridePolicy)
    {
        OwnedExtensionRegistration<T>? previous;
        OwnedExtensionRegistration<T> current;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
            if (!target.TryGetValue(key, out var stack)) target[key] = stack = new RegistrationStack<T>();
            previous = stack.Current;
            if (previous is not null && overridePolicy == ExtensionOverridePolicy.Reject)
                throw new InvalidOperationException($"{category} '{key}' is already registered by '{previous.SourceId}'.");
            current = new OwnedExtensionRegistration<T>(key, sourceId, value, overridePolicy);
            stack.Push(current);
        }
        Publish(new ExtensionRegistryChange(previous is null ? ExtensionRegistryChangeKind.Added : ExtensionRegistryChangeKind.Replaced, sourceId, category, key, value));
        return new RegistryHandle(() => RemoveStackEntry(target, key, category, current));
    }

    private void RemoveStackEntry<T>(Dictionary<string, RegistrationStack<T>> target, string key, string category, OwnedExtensionRegistration<T> registration)
    {
        OwnedExtensionRegistration<T>? removed;
        OwnedExtensionRegistration<T>? previousCurrent;
        OwnedExtensionRegistration<T>? restored;
        lock (_gate)
        {
            if (!target.TryGetValue(key, out var stack)) return;
            previousCurrent = stack.Current;
            removed = stack.Remove(registration);
            restored = stack.Current;
            if (stack.Count == 0) target.Remove(key);
        }
        if (removed is null) return;
        Publish(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Removed, removed.SourceId, category, key, removed.Value));
        if (ReferenceEquals(previousCurrent, removed) && restored is not null)
            Publish(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Restored, restored.SourceId, category, key, restored.Value));
    }

    private void Remove<T>(Dictionary<string, OwnedExtensionRegistration<T>> target, string key, string category)
    {
        OwnedExtensionRegistration<T>? removed = null;
        lock (_gate)
        {
            if (target.TryGetValue(key, out removed)) target.Remove(key);
        }
        if (removed is not null) Publish(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Removed, removed.SourceId, category, key, removed.Value));
    }

    private static int RemoveBySource<T>(Dictionary<string, OwnedExtensionRegistration<T>> target, string sourceId)
    {
        var keys = target.Where(pair => StringComparer.Ordinal.Equals(pair.Value.SourceId, sourceId)).Select(pair => pair.Key).ToArray();
        foreach (var key in keys) target.Remove(key);
        return keys.Length;
    }

    private static int RemoveBySourceStack<T>(Dictionary<string, RegistrationStack<T>> target, string sourceId, string category, List<ExtensionRegistryChange> changes)
    {
        var removedCount = 0;
        foreach (var (key, stack) in target.ToArray())
        {
            var previous = stack.Current;
            var removed = stack.RemoveBySource(sourceId);
            if (removed == 0) continue;
            removedCount += removed;
            var restored = stack.Current;
            if (stack.Count == 0) target.Remove(key);
            if (previous is not null && StringComparer.Ordinal.Equals(previous.SourceId, sourceId))
            {
                changes.Add(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Removed, previous.SourceId, category, key, previous.Value));
                if (restored is not null) changes.Add(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Restored, restored.SourceId, category, key, restored.Value));
            }
        }
        return removedCount;
    }

    private sealed class RegistrationStack<T>
    {
        private readonly List<OwnedExtensionRegistration<T>> _items = [];
        public int Count => _items.Count;
        public OwnedExtensionRegistration<T>? Current => _items.LastOrDefault();
        public IReadOnlyList<OwnedExtensionRegistration<T>> Items => _items.ToArray();
        public void Push(OwnedExtensionRegistration<T> registration) => _items.Add(registration);
        public OwnedExtensionRegistration<T>? Remove(OwnedExtensionRegistration<T> registration)
        {
            var index = _items.FindIndex(item => ReferenceEquals(item, registration));
            if (index < 0) return null;
            var removed = _items[index];
            _items.RemoveAt(index);
            return removed;
        }
        public int RemoveBySource(string sourceId)
        {
            var before = _items.Count;
            _items.RemoveAll(item => StringComparer.Ordinal.Equals(item.SourceId, sourceId));
            return before - _items.Count;
        }
    }

    private sealed class RegistryHandle(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }

    private sealed class CompositeHandle(IDisposable first, IDisposable? second) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            first.Dispose();
            second?.Dispose();
        }
    }
}
