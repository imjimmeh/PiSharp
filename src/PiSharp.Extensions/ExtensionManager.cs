using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Ai;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;

namespace PiSharp.Extensions;

public sealed record ExtensionRuntimeActions(
    string Cwd,
    bool HasUi,
    IExtensionUi Ui,
    Func<AgentMessage, CancellationToken, Task> SendMessageAsync,
    Func<CancellationToken, Task<string?>>? GetSessionNameAsync = null,
    Func<global::PiSharp.Agent.Core.Models.ModelDescriptor, CancellationToken, Task>? SetModelAsync = null,
    Func<ThinkingLevel, CancellationToken, Task>? SetThinkingLevelAsync = null)
{
    public ExtensionRuntimeBinding ToBinding()
    {
        var binding = new ExtensionRuntimeBinding(Cwd, HasUi, Ui)
        {
            SendMessageAsync = (message, _, _, token) => SendMessageAsync(message, token),
            GetSessionNameAsync = GetSessionNameAsync ?? (_ => Task.FromResult<string?>(null))
        };
        if (SetModelAsync is not null) binding.SetModelAsync = async (model, token) => { await SetModelAsync(model, token); return true; };
        if (SetThinkingLevelAsync is not null) binding.SetThinkingLevelAsync = SetThinkingLevelAsync;
        return binding;
    }
}

public sealed class ExtensionManager(ExtensionRegistry? registry = null)
{
    private readonly List<LoadedExtension> _loaded = [];
    public ExtensionRegistry Registry { get; } = registry ?? new ExtensionRegistry();
    public IReadOnlyList<LoadedExtension> Loaded => _loaded.ToArray();

    public async Task<LoadedExtension> InitializeAsync(
        ExtensionDescriptor descriptor,
        IExtension extension,
        ExtensionRuntimeActions actions,
        CancellationToken cancellationToken = default)
    {
        descriptor.Validate();
        var api = new ExtensionApi(descriptor, Registry, actions.ToBinding());
        await extension.InitializeAsync(api, cancellationToken);
        var loaded = new LoadedExtension(descriptor, extension);
        _loaded.Add(loaded);
        return loaded;
    }

    public async Task<LoadedExtension> InitializeAsync(
        ExtensionDescriptor descriptor,
        IExtension extension,
        ExtensionRuntimeBinding binding,
        CancellationToken cancellationToken = default)
    {
        descriptor.Validate();
        var api = new ExtensionApi(descriptor, Registry, binding);
        await extension.InitializeAsync(api, cancellationToken);
        var loaded = new LoadedExtension(descriptor, extension);
        _loaded.Add(loaded);
        return loaded;
    }

    public int Unload(string sourceId)
    {
        var removed = Registry.UnregisterBySource(sourceId);
        var providerRemoved = ApiRegistry.UnregisterBySource(sourceId);
        var modelRemoved = ModelRegistry.UnregisterBySource(sourceId);
        foreach (var provider in Registry.Providers)
        {
            var current = ApiRegistry.Get(provider.Value.Api);
            if (current?.Provider != provider.Value || !StringComparer.Ordinal.Equals(current.SourceId, provider.SourceId))
                PublicApi.RegisterProvider(provider.Value, provider.SourceId);
        }
        _loaded.RemoveAll(extension => StringComparer.Ordinal.Equals(extension.Descriptor.EffectiveSourceId, sourceId));
        return removed + providerRemoved + modelRemoved;
    }

    private sealed class ExtensionApi(ExtensionDescriptor descriptor, ExtensionRegistry registry, ExtensionRuntimeBinding binding) : IExtensionApi
    {
        public ExtensionDescriptor Descriptor { get; } = descriptor;
        public string Cwd => binding.Cwd;
        public bool HasUi => binding.HasUi;
        public IExtensionUi Ui => binding.Ui;
        public IExtensionSessionApi Session { get; } = binding.Session;
        public IExtensionToolApi Tools { get; } = new ToolApi(descriptor, registry, binding);
        public IExtensionSkillApi Skills { get; } = new SkillApi(descriptor, registry, binding);
        public IExtensionModelApi Model { get; } = binding.Model;
        public IExtensionSettingsApi Settings { get; } = new ExtensionScopedSettings(descriptor, binding.RuntimeSettings);
        public IExtensionStateApi State { get; } = new ExtensionScopedState(descriptor, binding.RuntimeState);
        public IExtensionEventBus Events { get; } = new ExtensionEventBus(registry, descriptor.EffectiveSourceId, binding.EmitEventAsync);
        public IExtensionPromptApi Prompt { get; } = new PromptApi(descriptor, registry);

        public IDisposable On(string eventName, ExtensionEventHandler handler)
            => registry.RegisterHandler(Descriptor.EffectiveSourceId, eventName, handler);

        public IDisposable Use(ExtensionMiddleware middleware)
            => registry.RegisterMiddleware(Descriptor.EffectiveSourceId, middleware);

        public IDisposable RegisterTool(ExtensionToolRegistration registration)
            => registry.RegisterTool(Descriptor.EffectiveSourceId, registration.ToAgentTool(), registration.Override);

        public IDisposable RegisterSkill(ExtensionSkillRegistration registration)
            => registry.RegisterSkill(Descriptor.EffectiveSourceId, registration, registration.Override);

        public IDisposable RegisterCommand(ExtensionCommandRegistration registration) => registry.RegisterCommand(Descriptor.EffectiveSourceId, registration);
        public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => registry.RegisterShortcut(Descriptor.EffectiveSourceId, registration);
        public IDisposable RegisterFlag(ExtensionFlagRegistration registration)
        {
            binding.RegisterFlag(registration);
            return registry.RegisterFlag(Descriptor.EffectiveSourceId, registration);
        }
        public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => registry.RegisterMessageRenderer(Descriptor.EffectiveSourceId, registration);
        public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => registry.RegisterMessageDecorator(Descriptor.EffectiveSourceId, registration);

        public RegisteredApiProvider RegisterProvider(IModelProvider provider)
        {
            registry.RegisterProvider(Descriptor.EffectiveSourceId, provider);
            return PublicApi.RegisterProvider(provider, Descriptor.EffectiveSourceId);
        }

        public bool RemoveProvider(string api) => ApiRegistry.Unregister(api, Descriptor.EffectiveSourceId);
        public object? GetFlag(string name) => binding.GetFlag(name);
        public IReadOnlyDictionary<string, object?> GetFlags() => binding.FlagValues;
        public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
            => binding.SendMessageAsync(message, delivery, triggerTurn, cancellationToken);
    }

    private sealed class ToolApi(ExtensionDescriptor descriptor, ExtensionRegistry registry, ExtensionRuntimeBinding binding) : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => registry.RegisterTool(descriptor.EffectiveSourceId, registration.ToAgentTool(), registration.Override);
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default) => binding.GetActiveToolsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default) => binding.GetAllToolsAsync(cancellationToken);
        public Task SetActiveToolsAsync(IReadOnlyList<string> toolNames, CancellationToken cancellationToken = default) => binding.SetActiveToolsAsync(toolNames, cancellationToken);
    }

    private sealed class SkillApi(ExtensionDescriptor descriptor, ExtensionRegistry registry, ExtensionRuntimeBinding binding) : IExtensionSkillApi
    {
        public IDisposable RegisterSkill(ExtensionSkillRegistration registration) => registry.RegisterSkill(descriptor.EffectiveSourceId, registration, registration.Override);
        public Task<IReadOnlyList<ExtensionSkillRegistration>> GetAllSkillsAsync(CancellationToken cancellationToken = default) => binding.GetAllSkillsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default) => binding.GetSelectedSkillsAsync(cancellationToken);
        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default) => binding.SetSelectedSkillsAsync(skillNames, cancellationToken);
    }

    private sealed class PromptApi(ExtensionDescriptor descriptor, ExtensionRegistry registry) : IExtensionPromptApi
    {
        public IDisposable RegisterContributor(IPromptContributor contributor) => registry.RegisterPromptContributor(descriptor.EffectiveSourceId, contributor);
        public IDisposable RegisterSection(PromptSection section) => registry.RegisterPromptSection(descriptor.EffectiveSourceId, section);
        public IDisposable RegisterSection(ExtensionPromptSectionRegistration registration) => registry.RegisterPromptSection(descriptor.EffectiveSourceId, registration.Section, registration.Override);
        public IDisposable RegisterTransform(IPromptTransform transform) => registry.RegisterPromptTransform(descriptor.EffectiveSourceId, transform);
    }
}

public sealed record LoadedExtension(ExtensionDescriptor Descriptor, IExtension Instance);
