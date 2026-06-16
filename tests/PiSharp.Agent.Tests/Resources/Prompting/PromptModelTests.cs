using PiSharp.Agent.Core.Prompting;
using Xunit;

namespace PiSharp.Agent.Tests.Resources.Prompting;

public sealed class PromptModelTests
{
    [Fact]
    public void PromptSectionStoresIdentityPlacementAndContent()
    {
        var section = new PromptSection("id", PromptSectionKind.Extension, new RawPromptContent("body"), new PromptPlacement("slot", 5));

        Assert.Equal("id", section.Id);
        Assert.Equal(PromptSectionKind.Extension, section.Kind);
        Assert.Equal("slot", section.Placement.Slot);
        Assert.Equal(5, section.Placement.Priority);
        Assert.IsType<RawPromptContent>(section.Content);
    }

    [Fact]
    public void CompositionContextIsImmutableRecord()
    {
        var context = NewContext() with { Cwd = "/other" };

        Assert.Equal("/other", context.Cwd);
        Assert.Equal(PromptMode.Default, context.Mode);
    }

    [Fact]
    public void ContributionSourcePreservesOwnershipMetadata()
    {
        var source = new PromptContributionSource("extension:test", PromptContributionSourceKind.Extension);

        Assert.Equal("extension:test", source.Id);
        Assert.Equal(PromptContributionSourceKind.Extension, source.Kind);
    }

    private static SystemPromptCompositionContext NewContext() => new(
        "/repo",
        new DateOnly(2026, 5, 26),
        PromptMode.Default,
        [],
        [],
        [],
        null,
        null,
        [],
        [],
        new PromptDocumentationPaths("README.md", "docs", "examples"));
}
