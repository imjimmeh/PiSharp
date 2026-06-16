using PiSharp.Agent.Core.Prompting;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class PromptExtensionApiTests
{
    [Fact]
    public void RegistryStoresPromptRegistrationsAndUnloadRemovesThem()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterPromptSection("extension:a", Section("a"));
        registry.RegisterPromptContributor("extension:a", new TestContributor());
        registry.RegisterPromptTransform("extension:a", new TestTransform());

        var removed = registry.UnregisterBySource("extension:a");

        Assert.Equal(3, removed);
        Assert.Empty(registry.PromptSections);
        Assert.Empty(registry.PromptContributors);
        Assert.Empty(registry.PromptTransforms);
    }

    [Fact]
    public void RegistryPromptSectionOverrideRestoresPreviousWinner()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterPromptSection("extension:base", Section("team-rules"));
        var handle = registry.RegisterPromptSection("extension:override", Section("team-rules"), ExtensionOverridePolicy.Override);

        Assert.Equal("extension:override", registry.PromptSections.Single().SourceId);
        handle.Dispose();

        Assert.Equal("extension:base", registry.PromptSections.Single().SourceId);
    }

    [Fact]
    public async Task ExtensionApiCanRegisterPromptArtifacts()
    {
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        await manager.InitializeAsync(
            new ExtensionDescriptor("prompt", "Prompt", "1.0.0"),
            new PromptExtension(),
            new ExtensionRuntimeActions("/repo", false, NoExtensionUi.Instance, (_, _) => Task.CompletedTask));

        Assert.Single(registry.PromptSections);
        Assert.Single(registry.PromptContributors);
        Assert.Single(registry.PromptTransforms);
    }

    private static PromptSection Section(string id)
        => new(id, PromptSectionKind.Extension, new RawPromptContent("body"), new PromptPlacement("footer"));

    private sealed class TestContributor : IPromptContributor
    {
        public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
        {
            yield return new PromptContribution(Section("contributed"), new PromptContributionSource("test", PromptContributionSourceKind.Extension));
        }
    }

    private sealed class TestTransform : IPromptTransform
    {
        public SystemPromptDocument Apply(SystemPromptDocument document, SystemPromptCompositionContext context) => document;
    }

    private sealed class PromptExtension : IExtension
    {
        public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
        {
            api.Prompt.RegisterSection(Section("registered"));
            api.Prompt.RegisterContributor(new TestContributor());
            api.Prompt.RegisterTransform(new TestTransform());
            return Task.CompletedTask;
        }
    }
}
