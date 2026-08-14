using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;
using PiSharp.Extensions;

namespace PiSharp.Extensions.Rules.Tests;

/// <summary>
/// Minimal in-memory <see cref="IExtensionApi"/> for unit tests: supports the flag surface,
/// event subscription, prompt contributors, and — crucially — a rules API whose
/// <c>GetAllRulesAsync</c> actually queries the registered providers (the behavior the real
/// runtime is expected to wire once the rules registry is bound). Unused surfaces throw.
/// </summary>
internal sealed class TestFakeApi : IExtensionApi
{
    private readonly List<(string EventName, ExtensionEventHandler Handler)> _handlers = [];
    private readonly FakePromptApi _prompt = new();
    private readonly Dictionary<string, object?> _flagValues = new(StringComparer.Ordinal);

    public string Cwd { get; set; } = "/";
    public bool HasUi { get; set; }
    public IExtensionUi Ui { get; set; } = NoExtensionUi.Instance;
    public ExtensionDescriptor Descriptor { get; set; } = new("pisharp-rules", "Rules", "1.0.0");

    public IReadOnlyList<(string EventName, ExtensionEventHandler Handler)> RegisteredHandlers => _handlers;
    public IReadOnlyList<IPromptContributor> RegisteredContributors => _prompt.Contributors;

    public IExtensionRuleApi Rules { get; } = new FakeRuleApi();
    public IExtensionPromptApi Prompt => _prompt;

    public IDisposable On(string eventName, ExtensionEventHandler handler)
    {
        _handlers.Add((eventName, handler));
        return new NullDisposable();
    }

    public IDisposable RegisterFlag(ExtensionFlagRegistration registration)
    {
        _flagValues.TryAdd(registration.Name, registration.DefaultValue);
        return new NullDisposable();
    }
    public Task SendMessageAsync(
        AgentMessage message,
        ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp,
        bool triggerTurn = false,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;


    public object? GetFlag(string name) => _flagValues.TryGetValue(name, out var value) ? value : null;

    public void SetFlag(string name, object? value) => _flagValues[name] = value;


    public IDisposable Use(ExtensionMiddleware middleware) => throw new NotSupportedException();
    public IDisposable RegisterTool(ExtensionToolRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterSkill(ExtensionSkillDefinition registration) => throw new NotSupportedException();
    public IDisposable RegisterCommand(ExtensionCommandRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterShortcut(ExtensionShortcutRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageRenderer(ExtensionMessageRendererRegistration registration) => throw new NotSupportedException();
    public IDisposable RegisterMessageDecorator(ExtensionMessageDecoratorRegistration registration) => throw new NotSupportedException();
    public RegisteredApiProvider RegisterProvider(IModelProvider provider) => throw new NotSupportedException();
    public bool RemoveProvider(string api) => throw new NotSupportedException();
    public IReadOnlyDictionary<string, object?> GetFlags() => _flagValues;

    public IExtensionSessionApi Session => throw new NotSupportedException();
    public IExtensionToolApi Tools => throw new NotSupportedException();
    public IExtensionSkillApi Skills => throw new NotSupportedException();
    public IExtensionModelApi Model => throw new NotSupportedException();
    public IExtensionEventBus Events => throw new NotSupportedException();
    public IExtensionSettingsApi Settings => throw new NotSupportedException();
    public IExtensionStateApi State => throw new NotSupportedException();

    internal sealed class FakeRuleApi : IExtensionRuleApi
    {
        private readonly List<IRuleProvider> _providers = [];

        public IReadOnlyList<IRuleProvider> Providers => _providers;

        public IDisposable RegisterProvider(IRuleProvider provider)
        {
            _providers.RemoveAll(p => p.Name == provider.Name);
            _providers.Add(provider);
            return new NullDisposable();
        }

        public async Task<IReadOnlyList<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
        {
            var all = new List<Rule>();
            foreach (var provider in _providers)
            {
                var discovered = await provider.DiscoverAsync(cancellationToken);
                all.AddRange(discovered);
            }
            return all;
        }

        public IReadOnlyList<string> GetProviderNames() => _providers.Select(p => p.Name).ToArray();
    }

    internal sealed class FakePromptApi : IExtensionPromptApi
    {
        private readonly List<IPromptContributor> _contributors = [];

        public IReadOnlyList<IPromptContributor> Contributors => _contributors;

        public IDisposable RegisterContributor(IPromptContributor contributor)
        {
            _contributors.Add(contributor);
            return new NullDisposable();
        }

        public IDisposable RegisterSection(PromptSection section) => new NullDisposable();
        public IDisposable RegisterSection(ExtensionPromptSectionRegistration registration) => new NullDisposable();
        public IDisposable RegisterTransform(IPromptTransform transform) => new NullDisposable();
    }

    internal sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
