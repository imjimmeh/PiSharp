using PiSharp.Agent.Core.Prompting;
using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class RulesPromptContributorTests
{
    [Fact]
    public void Contribute_YieldsAlwaysRulesSection()
    {
        var always = new Rule("RULES", "sticky body", Path: "u/RULES.md", ApplyMode: RuleApplyMode.Always);
        var stream = new Rule("no-todo", "x", TriggerPattern: "todo");
        var contributor = new RulesPromptContributor(() => [always, stream]);
        var context = new SystemPromptCompositionContext(
            "/", DateOnly.FromDateTime(DateTime.UtcNow), PromptMode.Default, [], [], [], null, null,
            [], [], new PromptDocumentationPaths("README.md", "docs", "examples"));

        var contributions = contributor.Contribute(context).ToArray();

        var contribution = Assert.Single(contributions);
        Assert.Equal(RulesPromptContributor.SectionId, contribution.Section.Id);
        Assert.Equal(PromptSectionKind.Instructions, contribution.Section.Kind);
        var markdown = Assert.IsType<MarkdownPromptContent>(contribution.Section.Content).Markdown;
        Assert.Contains("sticky body", markdown);
        Assert.DoesNotContain("no-todo", markdown); // stream-trigger rules excluded
    }

    [Fact]
    public void Contribute_NoAlwaysRulesYieldsNothing()
    {
        var stream = new Rule("no-todo", "x", TriggerPattern: "todo");
        var contributor = new RulesPromptContributor(() => [stream]);
        var context = new SystemPromptCompositionContext(
            "/", DateOnly.FromDateTime(DateTime.UtcNow), PromptMode.Default, [], [], [], null, null,
            [], [], new PromptDocumentationPaths("README.md", "docs", "examples"));

        Assert.Empty(contributor.Contribute(context));
    }
}
