using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Agent.Tests.Resources.Prompting;

public sealed class SystemPromptComposerTests
{
    [Fact]
    public void ComposeOrdersSectionsBySlotPriorityAndId()
    {
        var composer = new SystemPromptComposer([
            Contributor("b", "instructions", 10),
            Contributor("a", "header", 0),
            Contributor("c", "instructions", 0)
        ]);

        var document = composer.Compose(Context());

        Assert.Equal(["a", "c", "b"], document.Sections.Select(section => section.Id).ToArray());
    }

    [Fact]
    public void ComposeReportsDuplicateSectionFromDifferentSources()
    {
        var section = new PromptSection("same", PromptSectionKind.Extension, new RawPromptContent("one"), new PromptPlacement("header"));
        var composer = new SystemPromptComposer([
            new StaticPromptSectionContributor(section, new PromptContributionSource("extension:a", PromptContributionSourceKind.Extension)),
            new StaticPromptSectionContributor(section with { Content = new RawPromptContent("two") }, new PromptContributionSource("extension:b", PromptContributionSourceKind.Extension))
        ]);

        var document = composer.Compose(Context());

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "duplicate_section" && diagnostic.SectionId == "same");
        Assert.Single(document.Sections);
    }

    [Fact]
    public void BuildUsesRendererOutput()
    {
        var composer = new SystemPromptComposer([Contributor("a", "header", 0)]);

        Assert.Equal("a body", composer.Build(Context()));
    }

    [Fact]
    public void RenderUsesRendererWithoutRecomposing()
    {
        var composer = new SystemPromptComposer([]);
        var document = new SystemPromptDocument([
            new PromptSection("patched", PromptSectionKind.Extension, new RawPromptContent("patched body"), new PromptPlacement("header"))
        ], []);

        Assert.Equal("patched body", composer.Render(document));
    }

    [Fact]
    public void PromptDocumentPatchApplierReplacesSectionsAndPreservesOrdering()
    {
        var document = new SystemPromptDocument([
            new PromptSection("first", PromptSectionKind.Extension, new RawPromptContent("first"), new PromptPlacement("instructions", 10)),
            new PromptSection("skills.available", PromptSectionKind.Skills, new RawPromptContent("all skills"), new PromptPlacement("skills"))
        ], []);
        var applier = new PromptDocumentPatchApplier();

        var patched = applier.Apply(document, new PromptDocumentPatch(
            ReplaceSections: [new PromptDocumentSectionPatch("skills.available", "selected skills", Slot: "skills", Kind: "skills", ContentType: "raw")],
            AppendSections: [new PromptDocumentSectionPatch("extra", "extra", Slot: "instructions", Priority: 5)]));

        Assert.Equal(["extra", "first", "skills.available"], patched.Sections.Select(section => section.Id).ToArray());
        Assert.Equal("selected skills", Assert.IsType<RawPromptContent>(patched.Sections.Single(section => section.Id == "skills.available").Content).Text);
    }

    [Fact]
    public void PromptDocumentPatchApplierReportsProtectedSectionRemoval()
    {
        var document = new SystemPromptDocument([
            new PromptSection("protected", PromptSectionKind.Instructions, new RawPromptContent("must stay"), new PromptPlacement("instructions"), new PromptSectionOptions(Protected: true))
        ], []);
        var applier = new PromptDocumentPatchApplier();

        var patched = applier.Apply(document, new PromptDocumentPatch(RemoveSectionIds: ["protected"]));

        Assert.Contains(patched.Diagnostics, diagnostic => diagnostic.Code == "protected_section_removed" && diagnostic.SectionId == "protected");
    }

    private static IPromptContributor Contributor(string id, string slot, int priority)
        => new StaticPromptSectionContributor(
            new PromptSection(id, PromptSectionKind.Extension, new RawPromptContent($"{id} body"), new PromptPlacement(slot, priority)),
            new PromptContributionSource("test", PromptContributionSourceKind.BuiltIn));

    private static SystemPromptCompositionContext Context() => new(
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
