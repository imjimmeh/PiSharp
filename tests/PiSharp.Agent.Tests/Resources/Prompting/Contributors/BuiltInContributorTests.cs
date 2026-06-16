using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Resources.Prompting.Contributors;
using Xunit;

namespace PiSharp.Agent.Tests.Resources.Prompting.Contributors;

public sealed class BuiltInContributorTests
{
    [Fact]
    public void ToolGuidelinesDeduplicatesTrimmedGuidelines()
    {
        var contributor = new ToolGuidelinesPromptContributor();
        var context = Context(tools: [new ToolPromptInfo("read", "Read", [" Use read. ", "Use read."])], selected: ["read"]);

        var contribution = Assert.Single(contributor.Contribute(context));
        var prompt = MarkdownSystemPromptRenderer.Default.Render(new SystemPromptDocument([contribution.Section], []));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt, "- Use read\\."));
    }

    [Fact]
    public void ProjectContextUsesMarkdownForDefaultAndXmlForCustom()
    {
        var contributor = new ProjectContextPromptContributor();
        var defaultContribution = Assert.Single(contributor.Contribute(Context(contextFiles: [new SystemPromptContextFile("C:\\repo\\AGENTS.md", "rules")])));
        var customContribution = Assert.Single(contributor.Contribute(Context(mode: PromptMode.CustomReplacement, customPrompt: "custom", contextFiles: [new SystemPromptContextFile("/repo/AGENTS.md", "rules")])));

        var defaultPrompt = MarkdownSystemPromptRenderer.Default.Render(new SystemPromptDocument([defaultContribution.Section], []));
        var customPromptRendered = MarkdownSystemPromptRenderer.Default.Render(new SystemPromptDocument([customContribution.Section], []));

        Assert.Contains("# Project Context", defaultPrompt);
        Assert.Contains("## C:/repo/AGENTS.md", defaultPrompt);
        Assert.Contains("<project_context>", customPromptRendered);
    }

    [Fact]
    public void SkillsEmitOnlyWhenReadIsSelected()
    {
        var contributor = new SkillsPromptContributor();
        var context = Context(selected: ["bash"], skills: [new PromptSkillInfo("skill", "desc", "/skill/SKILL.md")]);

        Assert.Empty(contributor.Contribute(context));
    }

    private static SystemPromptCompositionContext Context(
        PromptMode mode = PromptMode.Default,
        string? customPrompt = null,
        IReadOnlyList<ToolPromptInfo>? tools = null,
        IReadOnlyList<string>? selected = null,
        IReadOnlyList<SystemPromptContextFile>? contextFiles = null,
        IReadOnlyList<PromptSkillInfo>? skills = null)
        => new(
            "/repo",
            new DateOnly(2026, 5, 26),
            mode,
            tools ?? [],
            selected ?? [],
            [],
            customPrompt,
            null,
            contextFiles ?? [],
            skills ?? [],
            new PromptDocumentationPaths("README.md", "docs", "examples"));
}
