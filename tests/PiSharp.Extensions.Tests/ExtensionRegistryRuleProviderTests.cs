using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

/// <summary>
/// Phase 1 (P10) registry tests: <c>rule-provider:{name}</c> keying + disposal,
/// <c>stream-delta:{sourceId}</c> single-slot semantics, and cleanup by source.
/// </summary>
public sealed class ExtensionRegistryRuleProviderTests
{
    [Fact]
    public void RegisterRuleProviderKeysByNameAndDisposalRemovesIt()
    {
        var registry = new ExtensionRegistry();

        var handle = registry.RegisterRuleProvider("extension:rules", new FakeRuleProvider("rules-dir", 100, []));

        var registration = Assert.Single(registry.RuleProviders);
        Assert.Equal("rule-provider:rules-dir", registration.Id);
        Assert.Equal("extension:rules", registration.SourceId);

        handle.Dispose();
        Assert.Empty(registry.RuleProviders);
    }

    [Fact]
    public void RuleProvidersFromDistinctNamesCoexist()
    {
        var registry = new ExtensionRegistry();

        registry.RegisterRuleProvider("extension:rules", new FakeRuleProvider("rules-dir", 100, []));
        registry.RegisterRuleProvider("extension:foreign", new FakeRuleProvider("cursor", 200, []));

        Assert.Equal(2, registry.RuleProviders.Count);
        Assert.Contains(registry.RuleProviders, r => r.Id == "rule-provider:rules-dir");
        Assert.Contains(registry.RuleProviders, r => r.Id == "rule-provider:cursor");
    }

    [Fact]
    public void RegisterRuleProviderRejectsEmptyName()
    {
        var registry = new ExtensionRegistry();

        Assert.Throws<ArgumentException>(() => registry.RegisterRuleProvider("extension:rules", new FakeRuleProvider(" ", 100, [])));
    }

    [Fact]
    public void StreamDeltaInterceptorIsSingleSlotPerSourceAndReplacementReplaces()
    {
        var registry = new ExtensionRegistry();
        var first = new FakeInterceptor();
        var second = new FakeInterceptor();

        registry.RegisterStreamDeltaInterceptor("rules", first);
        var replacement = registry.RegisterStreamDeltaInterceptor("rules", second);

        var registration = Assert.Single(registry.StreamDeltaInterceptors);
        Assert.Equal("stream-delta:rules", registration.Id);
        Assert.Same(second, registration.Value);

        replacement.Dispose();
        Assert.Empty(registry.StreamDeltaInterceptors);
    }

    [Fact]
    public void StreamDeltaInterceptorsFromDistinctSourcesCoexist()
    {
        var registry = new ExtensionRegistry();

        registry.RegisterStreamDeltaInterceptor("rules", new FakeInterceptor());
        registry.RegisterStreamDeltaInterceptor("safety", new FakeInterceptor());

        Assert.Equal(2, registry.StreamDeltaInterceptors.Count);
        Assert.Contains(registry.StreamDeltaInterceptors, r => r.Id == "stream-delta:rules");
        Assert.Contains(registry.StreamDeltaInterceptors, r => r.Id == "stream-delta:safety");
    }

    [Fact]
    public void UnregisterBySourceCleansRuleProvidersAndStreamDeltaInterceptors()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterRuleProvider("extension:a", new FakeRuleProvider("rules-dir", 100, []));
        registry.RegisterRuleProvider("extension:b", new FakeRuleProvider("cursor", 200, []));
        registry.RegisterStreamDeltaInterceptor("extension:a", new FakeInterceptor());
        registry.RegisterStreamDeltaInterceptor("extension:b", new FakeInterceptor());

        var removed = registry.UnregisterBySource("extension:a");

        Assert.Equal(2, removed);
        Assert.Single(registry.RuleProviders);
        Assert.Equal("extension:b", Assert.Single(registry.RuleProviders).SourceId);
        Assert.Single(registry.StreamDeltaInterceptors);
        Assert.Equal("stream-delta:extension:b", Assert.Single(registry.StreamDeltaInterceptors).Id);
    }

    [Fact]
    public void AllSourceIdsIncludesBothRuleProvidersAndStreamDeltaSources()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterRuleProvider("extension:rules", new FakeRuleProvider("rules-dir", 100, []));
        registry.RegisterStreamDeltaInterceptor("extension:engine", new FakeInterceptor());

        var sources = registry.SourceIds;

        Assert.Contains("extension:rules", sources);
        Assert.Contains("extension:engine", sources);
    }

    private sealed class FakeRuleProvider(string name, int priority, IReadOnlyList<Rule> rules) : IRuleProvider
    {
        public string Name { get; } = name;
        public int Priority { get; } = priority;
        public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(rules);
    }

    private sealed class FakeInterceptor : IStreamDeltaInterceptor
    {
        public Task<StreamDeltaDecision?> InterceptDeltaAsync(StreamDeltaContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<StreamDeltaDecision?>(null);

        public Task<IReadOnlyList<AgentMessage>> PrepareMessagesAsync(
            IReadOnlyList<AgentMessage> messages,
            AgentContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(messages);
    }
}
