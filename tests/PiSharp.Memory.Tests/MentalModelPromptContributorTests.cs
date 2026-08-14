using PiSharp.Agent.Core.Prompting;
using PiSharp.Memory.Abstractions;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class MentalModelPromptContributorTests
{
    private static SystemPromptCompositionContext Context() => new(
        Cwd: "/",
        CurrentDate: DateOnly.FromDateTime(DateTime.UtcNow),
        Mode: PromptMode.Default,
        Tools: [],
        SelectedToolNames: [],
        ExplicitGuidelines: [],
        CustomPrompt: null,
        AppendPrompt: null,
        ContextFiles: [],
        Skills: [],
        DocumentationPaths: new PromptDocumentationPaths("README.md", "docs", "examples"));

    private static IReadOnlyList<MemoryRecord> Records(int count)
        => Enumerable.Range(1, count)
            .Select(i => new MemoryRecord(
                $"mental-models/model-{i}",
                MemoryKind.MentalModel,
                $"Model {i}",
                $"Content {i}",
                [],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow))
            .ToArray();

    [Fact]
    public void Contribute_EmptyCache_YieldsNothing()
    {
        var marked = false;
        var contributor = new MentalModelPromptContributor(() => [], () => false, () => marked = true);

        Assert.Empty(contributor.Contribute(Context()));
        Assert.False(marked);
    }

    [Fact]
    public void Contribute_NullCache_YieldsNothing()
    {
        var contributor = new MentalModelPromptContributor(() => null, () => false, () => { });

        Assert.Empty(contributor.Contribute(Context()));
    }

    [Fact]
    public void Contribute_Records_YieldsSingleProjectContextSection()
    {
        var marked = false;
        var contributor = new MentalModelPromptContributor(() => Records(2), () => false, () => marked = true);

        var contributions = contributor.Contribute(Context()).ToArray();
        var section = Assert.Single(contributions).Section;
        Assert.Equal(MentalModelPromptContributor.SectionId, section.Id);
        Assert.Equal(PromptSectionKind.ProjectContext, section.Kind);
        Assert.True(section.Options?.OmitWhenEmpty);
        Assert.True(marked);

        var markdown = Assert.IsType<MarkdownPromptContent>(section.Content).Markdown;
        Assert.True(section.Options?.OmitWhenEmpty ?? false);
        Assert.Contains("mental-models/model-2", markdown);
        Assert.Contains("## Project memory (mental models)", markdown);
    }

    [Fact]
    public void Contribute_AfterInjection_YieldsNothing()
    {
        var contributor = new MentalModelPromptContributor(() => Records(1), () => true, () => { });

        Assert.Empty(contributor.Contribute(Context()));
    }

    [Fact]
    public void Render_TruncatesContentToSingleLine()
    {
        var records = new[]
        {
            new MemoryRecord("mental-models/m1", MemoryKind.MentalModel, "M", "line one\nline two", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var markdown = MentalModelPromptContributor.Render(records);
        Assert.Contains("line one line two", markdown);
    }
}
