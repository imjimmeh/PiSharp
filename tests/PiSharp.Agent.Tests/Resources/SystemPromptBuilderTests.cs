using PiSharp.Agent.Resources;
using Xunit;

namespace PiSharp.Agent.Tests.Resources;

public sealed class SystemPromptBuilderTests
{
    [Fact]
    public void BuildDefaultPromptIncludesPersonaToolsGuidelinesDocsDateAndCwd()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo/project",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: [new ToolPromptInfo("read", "Read file contents", ["Use read before editing."])],
            SelectedToolNames: ["read"]));

        Assert.Contains("You are an expert coding assistant operating inside pi", prompt);
        Assert.Contains("Available tools:\n- read: Read file contents", prompt);
        Assert.Contains("Guidelines:", prompt);
        Assert.Contains("- Use read before editing.", prompt);
        Assert.Contains("- Be concise in your responses", prompt);
        Assert.Contains("Pi documentation", prompt);
        Assert.Contains("Current date: 2026-05-26", prompt);
        Assert.Contains("Current working directory: /repo/project", prompt);
    }

    [Fact]
    public void BuildDefaultPromptShowsNoneWhenNoVisibleToolSnippetsExist()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: []));

        Assert.Contains("Available tools:\n(none)", prompt);
        Assert.Contains("Show file paths clearly", prompt);
    }

    [Fact]
    public void BuildCustomPromptReplacesDefaultButKeepsAppendContextSkillsDateAndCwd()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            CustomPrompt: "CUSTOM BASE",
            AppendPrompt: "APPEND TEXT",
            ContextFiles: [new SystemPromptContextFile("/repo/AGENTS.md", "context body")],
            Skills: [new Skill("example", "Example skill", "body", "/repo/skills/example/SKILL.md")],
            Tools: [new ToolPromptInfo("read", "Read file contents", [])],
            SelectedToolNames: ["read"]));

        Assert.StartsWith("CUSTOM BASE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You are an expert coding assistant operating inside pi", prompt);
        Assert.Contains("APPEND TEXT", prompt);
        Assert.Contains("<project_context>", prompt);
        Assert.Contains("<project_instructions path=\"/repo/AGENTS.md\">", prompt);
        Assert.Contains("<available_skills>", prompt);
        Assert.Contains("Current date: 2026-05-26", prompt);
        Assert.Contains("Current working directory: /repo", prompt);
    }

    [Fact]
    public void BuildDefaultPromptShowsAllSkillsWhenSelectedSkillNamesIsNull()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: [new ToolPromptInfo("read", "Read file contents", [])],
            SelectedToolNames: ["read"],
            Skills: [
                new Skill("alpha", "Alpha skill", "body", "/repo/skills/alpha/SKILL.md"),
                new Skill("beta", "Beta skill", "body", "/repo/skills/beta/SKILL.md")
            ]));

        Assert.Contains("<name>alpha</name>", prompt);
        Assert.Contains("<name>beta</name>", prompt);
    }

    [Fact]
    public void BuildDefaultPromptFiltersSkillsWhenSelectedSkillNamesAreProvided()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: [new ToolPromptInfo("read", "Read file contents", [])],
            SelectedToolNames: ["read"],
            Skills: [
                new Skill("alpha", "Alpha skill", "body", "/repo/skills/alpha/SKILL.md"),
                new Skill("beta", "Beta skill", "body", "/repo/skills/beta/SKILL.md")
            ],
            SelectedSkillNames: ["beta"]));

        Assert.Contains("<name>beta</name>", prompt);
        Assert.DoesNotContain("<name>alpha</name>", prompt);
    }

    [Fact]
    public void BuildDefaultPromptHidesSkillsWhenSelectedSkillNamesIsEmpty()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: [new ToolPromptInfo("read", "Read file contents", [])],
            SelectedToolNames: ["read"],
            Skills: [new Skill("alpha", "Alpha skill", "body", "/repo/skills/alpha/SKILL.md")],
            SelectedSkillNames: []));

        Assert.DoesNotContain("<available_skills>", prompt);
    }

    [Fact]
    public void BuildDefaultPromptRespectsDisableModelInvocationBeforeSelectedSkillFiltering()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: [new ToolPromptInfo("read", "Read file contents", [])],
            SelectedToolNames: ["read"],
            Skills: [new Skill("alpha", "Alpha skill", "body", "/repo/skills/alpha/SKILL.md", DisableModelInvocation: true)],
            SelectedSkillNames: ["alpha"]));

        Assert.DoesNotContain("<available_skills>", prompt);
    }

    [Fact]
    public void BuildEmptyCustomPromptIsIntentionalReplacement()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            CustomPrompt: string.Empty));

        Assert.DoesNotContain("You are an expert coding assistant operating inside pi", prompt);
        Assert.Contains("Current date: 2026-05-26", prompt);
    }

    [Fact]
    public void BuildDefaultPromptFormatsProjectContextLikeJavascriptDefaultBranch()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            ContextFiles: [new SystemPromptContextFile("/repo/AGENTS.md", "rules")],
            Tools: []));

        Assert.Contains("# Project Context", prompt);
        Assert.Contains("## /repo/AGENTS.md", prompt);
        Assert.Contains("rules", prompt);
    }

    [Fact]
    public void BuildDeduplicatesAndTrimsGuidelines()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            Tools: [new ToolPromptInfo("dynamic", null, [" Use dynamic. ", "Use dynamic.", " "])],
            SelectedToolNames: ["dynamic"]));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt, "- Use dynamic\\."));
    }
}
