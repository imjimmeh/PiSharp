using System.Text.Json;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Tools;
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
        public IExtensionPackageApi Packages => binding.Packages;
        public IExtensionModelApi Model { get; } = binding.Model;
        public IExtensionSettingsApi Settings { get; } = new ExtensionScopedSettings(descriptor, binding.RuntimeSettings);
        public IExtensionStateApi State { get; } = new ExtensionScopedState(descriptor, binding.RuntimeState);
        public IExtensionEventBus Events { get; } = new ExtensionEventBus(registry, descriptor.EffectiveSourceId, binding.EmitEventAsync);
        public IExtensionPromptApi Prompt { get; } = new PromptApi(descriptor, registry);
        public IExtensionCompletionApi Completion { get; } = new CompletionApi(binding);
        public IExtensionFileApi Files { get; } = new FileApi(binding);
        public IExtensionSearchApi Search { get; } = new SearchApi(binding);
        public IExtensionUrlApi Urls { get; } = new UrlApi(binding);
        public IExtensionRuleApi Rules { get; } = new RuleApi(descriptor, registry, binding);
        public IStreamDeltaInterceptorApi? StreamDelta { get; } = new StreamDeltaApi(descriptor, registry);
        public IExecutionEnv? ExecutionEnv => binding.ExecutionEnv;
        public IExtensionTelemetryApi Telemetry => binding.Telemetry;

        public IDisposable On(string eventName, ExtensionEventHandler handler)
            => registry.RegisterHandler(Descriptor.EffectiveSourceId, eventName, handler);

        public IDisposable Use(ExtensionMiddleware middleware)
            => registry.RegisterMiddleware(Descriptor.EffectiveSourceId, middleware);

        public IDisposable RegisterTool(ExtensionToolRegistration registration)
            => registry.RegisterTool(Descriptor.EffectiveSourceId, registration.ToAgentTool(), registration.Override);

        public IDisposable RegisterSkill(ExtensionSkillDefinition registration)
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
        public Task<IReadOnlyList<ExtensionCommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default)
            => binding.GetCommandsAsync(cancellationToken);
        public Task EmitClientEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
            => binding.EmitClientEventAsync(eventName, payload, cancellationToken);
    }

    private sealed class CompletionApi(ExtensionRuntimeBinding binding) : IExtensionCompletionApi
    {
        public Task<ExtensionCompletionResult> CompleteSimpleAsync(
            string provider, string modelId, string prompt,
            ExtensionCompleteRequest? options = null,
            CancellationToken cancellationToken = default)
            => binding.CompleteSimpleAsync(provider, modelId, prompt, options, cancellationToken);

        public Task<ExtensionCompletionResult> CompleteAsync(
            string provider, string modelId,
            IReadOnlyList<AgentMessage>? messages, string? systemPrompt = null,
            ExtensionCompleteRequest? options = null,
            CancellationToken cancellationToken = default)
            => binding.CompleteAsync(provider, modelId, messages, systemPrompt, options, false, cancellationToken);

        public IAsyncEnumerable<ExtensionCompletionDelta> StreamAsync(
            string provider, string modelId,
            IReadOnlyList<AgentMessage>? messages, string? systemPrompt = null,
            ExtensionCompleteRequest? options = null,
            CancellationToken cancellationToken = default)
            => binding.StreamAsync(provider, modelId, messages, systemPrompt, options, false, cancellationToken);
    }

    private sealed class UrlApi(ExtensionRuntimeBinding binding) : IExtensionUrlApi
    {
        public void RegisterResolver(IInternalUrlResolver resolver, bool overrideExisting = false)
        {
            if (binding.UrlRegistry is null) throw new NotSupportedException("This extension host does not provide an internal URL registry.");
            binding.UrlRegistry.Register(resolver, overrideExisting);
        }

        public IReadOnlyList<string> Schemes => binding.UrlRegistry?.Schemes ?? [];
    }

    private sealed class FileApi(ExtensionRuntimeBinding binding) : IExtensionFileApi
    {
        public IDisposable RegisterContentExtractor(IFileContentExtractor extractor, bool overrideExisting = false)
        {
            if (binding.FileContentExtractors is null) throw new NotSupportedException("This extension host does not provide a file-content extractor registry.");
            binding.FileContentExtractors.Register(extractor, overrideExisting);
            return new DisposableAction(() => binding.FileContentExtractors.Unregister(extractor.Id));
        }

        public IReadOnlyList<IFileContentExtractor> ContentExtractors => binding.FileContentExtractors?.Extractors ?? [];
    }

    private sealed class SearchApi(ExtensionRuntimeBinding binding) : IExtensionSearchApi
    {
        public IDisposable RegisterProvider(ISearchProvider provider, bool overrideExisting = false)
        {
            if (binding.SearchProviders is null) throw new NotSupportedException("This extension host does not provide a search provider registry.");
            binding.SearchProviders.Register(provider, overrideExisting);
            return new DisposableAction(() => binding.SearchProviders.Unregister(provider.Id));
        }

        public IReadOnlyList<ISearchProvider> Providers => binding.SearchProviders?.Providers ?? [];
        public ISearchProvider? GetProvider(string providerId) => binding.SearchProviders?.TryGet(providerId);
    }

    private sealed class RuleApi(ExtensionDescriptor descriptor, ExtensionRegistry registry, ExtensionRuntimeBinding binding) : IExtensionRuleApi
    {
        public IDisposable RegisterProvider(IRuleProvider provider)
            => registry.RegisterRuleProvider(descriptor.EffectiveSourceId, provider);

        public Task<IReadOnlyList<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
            => binding.GetAllRulesAsync(cancellationToken);

        public IReadOnlyList<string> GetProviderNames()
            => registry.GetRuleProviderNames();
    }

    private sealed class StreamDeltaApi(ExtensionDescriptor descriptor, ExtensionRegistry registry) : IStreamDeltaInterceptorApi
    {
        public IDisposable RegisterInterceptor(IStreamDeltaInterceptor interceptor)
            => registry.RegisterStreamDeltaInterceptor(descriptor.EffectiveSourceId, interceptor);
    }

    private sealed class ToolApi(ExtensionDescriptor descriptor, ExtensionRegistry registry, ExtensionRuntimeBinding binding) : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => registry.RegisterTool(descriptor.EffectiveSourceId, registration.ToAgentTool(), registration.Override);
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default) => binding.GetActiveToolsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default) => binding.GetAllToolsAsync(cancellationToken);
        public Task<AgentToolResult<object?>> ExecuteToolAsync(string toolName, JsonElement parameters, CancellationToken cancellationToken = default)
            => binding.ExecuteToolByNameAsync(toolName, parameters, cancellationToken);
        public Task SetActiveToolsAsync(IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default) => binding.SetActiveToolsAsync(toolNames, cancellationToken);
    }

    private sealed class SkillApi(ExtensionDescriptor descriptor, ExtensionRegistry registry, ExtensionRuntimeBinding binding) : IExtensionSkillApi
    {
        public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => registry.RegisterSkill(descriptor.EffectiveSourceId, registration, registration.Override);
        public IDisposable RegisterSkillProvider(ISkillProvider provider) => registry.RegisterSkillProvider(descriptor.EffectiveSourceId, provider);
        public Task<IReadOnlyList<ExtensionSkillDefinition>> GetAllSkillsAsync(CancellationToken cancellationToken = default) => binding.GetAllSkillsAsync(cancellationToken);
        public Task<IReadOnlyList<string>> GetSelectedSkillsAsync(CancellationToken cancellationToken = default) => binding.GetSelectedSkillsAsync(cancellationToken);
        public Task SetSelectedSkillsAsync(IReadOnlyList<string> skillNames, CancellationToken cancellationToken = default) => binding.SetSelectedSkillsAsync(skillNames, cancellationToken);
        public IExtensionManagedSkillApi ManagedSkills { get; } = new BindingManagedSkillApiAccessor(binding);
    }

    private sealed class BindingManagedSkillApiAccessor(ExtensionRuntimeBinding binding) : IExtensionManagedSkillApi
    {
        public Task<ManagedSkillDescriptor> CreateAsync(ManagedSkillCreateRequest request, CancellationToken ct = default) => binding.ManagedSkillCreateAsync(request, ct);
        public Task<ManagedSkillDescriptor> UpdateAsync(string name, ManagedSkillUpdateRequest request, CancellationToken ct = default) => binding.ManagedSkillUpdateAsync(name, request, ct);
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default) => binding.ManagedSkillDeleteAsync(name, ct);
        public Task<IReadOnlyList<ManagedSkillDescriptor>> ListAsync(CancellationToken ct = default) => binding.ManagedSkillListAsync(ct);
        public Task<ManagedSkillDescriptor> PromoteAsync(string sourceReference, CancellationToken ct = default) => binding.ManagedSkillPromoteAsync(sourceReference, ct);
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
