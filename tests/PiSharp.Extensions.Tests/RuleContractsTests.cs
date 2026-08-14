using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Phase 1 (P10) contract tests: <see cref="Rule"/>, <see cref="IRuleProvider"/>,
/// and <see cref="IExtensionRuleApi"/> shape, last-wins provider replacement with a
/// warning, and the no-op defaults of an unbound rule API.
/// </summary>
public sealed class RuleContractsTests
{
    [Fact]
    public void RuleDefaultsToStreamTriggerWithNoPathOrTriggerPattern()
    {
        var rule = new Rule("no-todo", "body");

        Assert.Equal("no-todo", rule.Name);
        Assert.Equal("body", rule.Content);
        Assert.Null(rule.Path);
        Assert.Equal(0, rule.Priority);
        Assert.Equal(RuleApplyMode.StreamTrigger, rule.ApplyMode);
        Assert.Null(rule.TriggerPattern);
        Assert.True(rule.IsStreamTrigger);
    }

    [Fact]
    public void RulePreservesAllConstructorValues()
    {
        var rule = new Rule("sticky", "content", "/repo/RULES.md", 100, RuleApplyMode.Always, "todo list");

        Assert.Equal("sticky", rule.Name);
        Assert.Equal("content", rule.Content);
        Assert.Equal("/repo/RULES.md", rule.Path);
        Assert.Equal(100, rule.Priority);
        Assert.Equal(RuleApplyMode.Always, rule.ApplyMode);
        Assert.Equal("todo list", rule.TriggerPattern);
        Assert.False(rule.IsStreamTrigger);
    }

    [Fact]
    public void RegisterRuleProviderReplacesDuplicateNameWithWarning()
    {
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = new RecordingLoggerFactory(provider);
        var registry = new ExtensionRegistry(loggerFactory: loggerFactory);

        using var first = registry.RegisterRuleProvider("source:a", new FakeRuleProvider("rules-dir", 100, []));
        using var second = registry.RegisterRuleProvider("source:b", new FakeRuleProvider("rules-dir", 50, []));

        var registration = Assert.Single(registry.RuleProviders);
        Assert.Equal("source:b", registration.SourceId);
        Assert.Equal("rules-dir", registration.Value.Name);
        Assert.Equal(50, registration.Value.Priority);
        Assert.Contains(provider.Messages, message => message.Contains("already registered", StringComparison.Ordinal) && message.Contains("rules-dir", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposingReplacementRestoresPreviousRuleProvider()
    {
        var registry = new ExtensionRegistry();
        var first = registry.RegisterRuleProvider("source:a", new FakeRuleProvider("rules-dir", 100, []));
        var second = registry.RegisterRuleProvider("source:b", new FakeRuleProvider("rules-dir", 50, []));

        second.Dispose();

        var restored = Assert.Single(registry.RuleProviders);
        Assert.Equal("source:a", restored.SourceId);
        Assert.Equal(100, restored.Value.Priority);

        first.Dispose();
        Assert.Empty(registry.RuleProviders);
    }

    [Fact]
    public async Task RuleApiExposesRegisteredProvidersAndTheirRules()
    {
        var registry = new ExtensionRegistry();
        var binding = new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance)
        {
            GetRuleProviderNamesAsync = _ => Task.FromResult<IReadOnlyList<string>>(registry.RuleProviders.Select(r => r.Value.Name).ToArray()),
            GetAllRulesAsync = _ => Task.FromResult<IReadOnlyList<Rule>>(
                registry.RuleProviders.SelectMany(r => r.Value.DiscoverAsync().GetAwaiter().GetResult()).ToArray())
        };
        var manager = new ExtensionManager(registry);
        var extension = new RuleProbeExtension();

        await manager.InitializeAsync(new ExtensionDescriptor("probe", "Probe", "1.0.0"), extension, binding);

        var handle = extension.Api!.Rules.RegisterProvider(new FakeRuleProvider("rules-dir", 100, [new Rule("no-todo", "body")]));

        Assert.Equal(["rules-dir"], extension.Api.Rules.GetProviderNames());
        var rule = Assert.Single(await extension.Api.Rules.GetAllRulesAsync());
        Assert.Equal("no-todo", rule.Name);

        handle.Dispose();
        Assert.Empty(extension.Api.Rules.GetProviderNames());
    }

    [Fact]
    public async Task RuleApiDefaultsAreNoOpsWhenBindingIsUnbound()
    {
        var manager = new ExtensionManager();
        var extension = new RuleProbeExtension();

        await manager.InitializeAsync(
            new ExtensionDescriptor("probe", "Probe", "1.0.0"),
            extension,
            new ExtensionRuntimeBinding("/repo", false, NoExtensionUi.Instance));

        Assert.Empty(await extension.Api!.Rules.GetAllRulesAsync());
        Assert.Empty(extension.Api.Rules.GetProviderNames());

        var handle = extension.Api.Rules.RegisterProvider(new FakeRuleProvider("rules-dir", 100, []));
        Assert.NotNull(handle);
        handle.Dispose();
        Assert.Empty(extension.Api.Rules.GetProviderNames());
        Assert.Empty(manager.Registry.RuleProviders);
    }

    private sealed class FakeRuleProvider(string name, int priority, IReadOnlyList<Rule> rules) : IRuleProvider
    {
        public string Name { get; } = name;
        public int Priority { get; } = priority;
        public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(rules);
    }

    private sealed class RuleProbeExtension : IExtension
    {
        public IExtensionApi? Api { get; private set; }

        public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
        {
            Api = api;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }

    private sealed class RecordingLoggerFactory(RecordingLoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
