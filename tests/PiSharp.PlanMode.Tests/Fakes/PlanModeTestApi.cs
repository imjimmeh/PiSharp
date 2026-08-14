using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.PlanMode.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExtensionApi"/> exposing exactly the surface the plan-mode
/// plugin consumes, following the PiSharp.Advisor.Tests <c>TestExtensionApi</c>
/// convention: concrete captured lists reachable from tests, unused members throw.
/// </summary>
public sealed class PlanModeTestApi : IExtensionApi
{
    public sealed record CapturedClientEvent(string Name, object? Payload);

    public List<ExtensionFlagRegistration> RegisteredFlags { get; } = [];
    public List<ExtensionCommandRegistration> RegisteredCommands { get; } = [];
    public List<ExtensionShortcutRegistration> RegisteredShortcuts { get; } = [];
    public List<IPromptContributor> RegisteredPromptContributors { get; } = [];
    public List<(string EventName, ExtensionEventHandler Handler)> RegisteredHandlers { get; } = [];
    public List<CapturedClientEvent> ClientEvents { get; } = [];
    public List<AgentMessage> SentMessages { get; } = [];
    public List<IReadOnlyList<string>?> SetActiveToolsCalls { get; } = [];
    public List<ModelDescriptor> SetModelCalls { get; } = [];
    public int DisposedSubscriptions { get; private set; }

    public string Cwd { get; set; } = "C:/project";
    public bool HasUi { get; set; }
    public IExtensionUi Ui => NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; set; } = new("plan-mode", "PiSharp Plan Mode", "0.1.0");

    // Tool surface.
    public IReadOnlyList<string> RegisteredToolNames { get; set; } = ["read", "grep", "find", "ls", "edit", "write", "bash"];
    public IReadOnlyList<string>? ActiveToolNames { get; set; }

    // Model surface.
    public ModelDescriptor? CurrentModel { get; set; } = new("test", "original-model", "api");

    // Flag surface.
    public Dictionary<string, object?> FlagValues { get; } = new(StringComparer.Ordinal);

    public string? SessionName { get; set; } = "session-abcdefgh";

    public IExtensionSettingsApi Settings { get; set; } = new InMemorySettingsApi();

    public IExtensionSessionApi Session => new SessionApi(this);
    public IExtensionToolApi Tools => new ToolApi(this);
    public IExtensionModelApi Model => new ModelApi(this);
    public IExtensionPromptApi Prompt => new PromptApi(this);
    public IExtensionEventBus Events => new EventBusApi(this);
    public IExtensionSkillApi Skills => new SkillApi();
    public IExtensionStateApi State => throw new NotSupportedException("PlanModeTestApi.State is not supported.");

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        RegisteredHandlers.Add((eventName, handler));
        return TrackDisposable();
    }

    /// <summary>Invokes every handler registered for <paramref name="eventName"/>.</summary>
    public async Task RaiseAsync(string eventName, AgentHarnessEvent original, object? payload)
    {
        var evt = new ExtensionEvent(eventName, original, payload);
        foreach (var (name, handler) in RegisteredHandlers.ToArray())
        {
            if (name == eventName) await handler(evt, CancellationToken.None);
        }
    }

    public IDisposable Use(ExtensionMiddleware middleware) => TrackDisposable();

    public IDisposable RegisterTool(ExtensionToolRegistration registration) => TrackDisposable();
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => TrackDisposable();

    public IDisposable RegisterCommand(ExtensionCommandRegistration registration)
    {
        RegisteredCommands.Add(registration);
        return TrackDisposable();
    }

    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration)
    {
        RegisteredShortcuts.Add(registration);
        return TrackDisposable();
    }

    public IDisposable RegisterFlag(ExtensionFlagRegistration registration)
    {
        RegisteredFlags.Add(registration);
        return TrackDisposable();
    }

    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => TrackDisposable();
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => TrackDisposable();
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException("PlanModeTestApi.RegisterProvider is not supported.");
    public bool RemoveProvider(string api) => throw new NotSupportedException("PlanModeTestApi.RemoveProvider is not supported.");

    public object? GetFlag(string name) => FlagValues.TryGetValue(name, out var value) ? value : null;
    public IReadOnlyDictionary<string, object?> GetFlags() => FlagValues;

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

    private IDisposable TrackDisposable() => new TrackingDisposable(this);

    private void OnSubscriptionDisposed() => DisposedSubscriptions++;

    private sealed class TrackingDisposable(PlanModeTestApi api) : IDisposable
    {
        public void Dispose() => api.OnSubscriptionDisposed();
    }

    private sealed class ToolApi(PlanModeTestApi api) : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => api.RegisterTool(registration);
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(api.ActiveToolNames ?? api.RegisteredToolNames);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(api.RegisteredToolNames);
        public Task SetActiveToolsAsync(IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default)
        {
            api.SetActiveToolsCalls.Add(toolNames);
            api.ActiveToolNames = toolNames;
            return Task.CompletedTask;
        }
    }

    private sealed class ModelApi(PlanModeTestApi api) : IExtensionModelApi
    {
        public Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
        {
            api.SetModelCalls.Add(model);
            api.CurrentModel = model;
            return Task.FromResult(true);
        }

        public Task<ModelDescriptor?> GetModelAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(api.CurrentModel);

        public Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ThinkingLevel?>(null);

        public Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class PromptApi(PlanModeTestApi api) : IExtensionPromptApi
    {
        public IDisposable RegisterContributor(IPromptContributor contributor)
        {
            api.RegisteredPromptContributors.Add(contributor);
            return api.TrackDisposable();
        }

        public IDisposable RegisterSection(PromptSection section) => api.TrackDisposable();
        public IDisposable RegisterSection(ExtensionPromptSectionRegistration registration) => api.TrackDisposable();
        public IDisposable RegisterTransform(IPromptTransform transform) => api.TrackDisposable();
    }

    private sealed class EventBusApi(PlanModeTestApi api) : IExtensionEventBus
    {
        public IDisposable On(string eventName, ExtensionEventHandler handler) => api.On(eventName, handler);
        public Task EmitAsync(string eventName, object payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SkillApi : IExtensionSkillApi
    {
        public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException("PlanModeTestApi.Skills is not supported.");
        public Task<IReadOnlyList<ExtensionSkillDefinition>> GetAllSkillsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("PlanModeTestApi.Skills is not supported.");
        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("PlanModeTestApi.Skills is not supported.");
        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("PlanModeTestApi.Skills is not supported.");
    }

    private sealed class SessionApi(PlanModeTestApi api) : IExtensionSessionApi
    {
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => api.SendMessageAsync(message, delivery, triggerTurn, cancellationToken);
        public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
            => api.SendMessageAsync(AgentMessages.User(content), delivery, false, cancellationToken);
        public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetNameAsync(CancellationToken cancellationToken = default) => Task.FromResult(api.SessionName);
        public Task SetNameAsync(string name, CancellationToken cancellationToken = default) { api.SessionName = name; return Task.CompletedTask; }
        public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class InMemorySettingsApi : IExtensionSettingsApi
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public object? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public T? Get<T>(string key)
        {
            var value = Get(key);
            return value is null ? default : System.Text.Json.JsonSerializer.Deserialize<T>(ExtensionSettingKeys.ToJsonNode(value)!.ToJsonString());
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

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
