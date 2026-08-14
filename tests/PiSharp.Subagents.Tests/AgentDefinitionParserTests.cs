using System.Text.Json;
using PiSharp.Abstractions.Options;
using PiSharp.Subagents.AgentDefinitions;
using Xunit;

namespace PiSharp.Subagents.Tests;

public sealed class AgentDefinitionParserTests
{
    private const string RepresentativeFrontmatter = """
        ---
        name: reviewer
        description: Reads a diff or plan and returns structured, evidence-backed review findings.
        systemPrompt: >-
          You are a rigorous code reviewer. Produce findings.
        tools: [read, grep, yield]
        spawns: []
        model: claude-haiku-4-5
        thinkingLevel: medium
        output:
          type: object
          required: [findings]
          properties:
            findings:
              type: array
              items:
                type: object
                required: [severity, summary]
                properties:
                  severity: { type: string, enum: [critical, warning, info] }
                  summary: { type: string }
                additionalProperties: false
              minItems: 1
        autoloadSkills: [my-domain]
        readSummarize: true
        hide: false
        ---

        (body text is ignored when systemPrompt is present)
        """;

    [Fact]
    public void ParseReadsRepresentativeFrontmatterIntoAgentDefinition()
    {
        var result = AgentDefinitionParser.Parse(RepresentativeFrontmatter, "/tmp/reviewer.md", AgentSourceKind.User);

        var definition = Assert.IsType<AgentDefinition>(result.Definition);
        Assert.Null(result.Diagnostic);
        Assert.Equal("reviewer", definition.Name);
        Assert.Equal("Reads a diff or plan and returns structured, evidence-backed review findings.", definition.Description);
        Assert.Equal("You are a rigorous code reviewer. Produce findings.", definition.SystemPrompt);
        Assert.Equal(["read", "grep", "yield"], definition.Tools);
        Assert.Empty(definition.Spawns);
        Assert.Equal("claude-haiku-4-5", definition.Model);
        Assert.Equal(ThinkingLevel.Medium, definition.ThinkingLevel);
        Assert.Equal(["my-domain"], definition.AutoloadSkills);
        Assert.True(definition.ReadSummarize);
        Assert.False(definition.Hide);
        Assert.Equal(AgentSourceKind.User, definition.Source);
        Assert.Equal("/tmp/reviewer.md", definition.SourcePath);
    }

    [Fact]
    public void ParseConvertsOutputSchemaToJsonDocument()
    {
        var result = AgentDefinitionParser.Parse(RepresentativeFrontmatter, "/tmp/reviewer.md", AgentSourceKind.User);

        var schema = Assert.IsType<AgentDefinition>(result.Definition).OutputSchema;
        Assert.NotNull(schema);
        Assert.Equal("object", schema!.Value.GetProperty("type").GetString());
        Assert.Equal("findings", schema!.Value.GetProperty("required")[0].GetString());
        var items = schema!.Value.GetProperty("properties").GetProperty("findings").GetProperty("items");
        Assert.Equal("warning", items.GetProperty("properties").GetProperty("severity").GetProperty("enum")[1].GetString());
        Assert.Equal(1, schema!.Value.GetProperty("properties").GetProperty("findings").GetProperty("minItems").GetInt32());
    }

    [Fact]
    public void ParseFallsBackToBodyAsSystemPrompt()
    {
        const string content = """
            ---
            name: scout
            description: Read-only explorer.
            ---

            You are a scout. Explore and report.
            """;

        var result = AgentDefinitionParser.Parse(content, "/tmp/scout.md", AgentSourceKind.Project);

        var definition = Assert.IsType<AgentDefinition>(result.Definition);
        Assert.Equal("You are a scout. Explore and report.", definition.SystemPrompt);
        Assert.Equal(["task"], definition.Spawns);
        Assert.Empty(definition.Tools);
        Assert.Null(definition.ThinkingLevel);
        Assert.False(definition.ReadSummarize);
    }

    [Fact]
    public void ParseMissingNameProducesDiagnostic()
    {
        const string content = """
            ---
            description: No name here.
            ---

            body
            """;

        var result = AgentDefinitionParser.Parse(content, "/tmp/bad.md", AgentSourceKind.Project);

        Assert.Null(result.Definition);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("missing_name", result.Diagnostic!.Code);
    }

    [Fact]
    public void ParseMissingDescriptionProducesDiagnostic()
    {
        const string content = """
            ---
            name: nameless-desc
            ---

            body
            """;

        var result = AgentDefinitionParser.Parse(content, "/tmp/bad.md", AgentSourceKind.Project);

        Assert.Null(result.Definition);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("missing_description", result.Diagnostic!.Code);
    }

    [Fact]
    public void ParseMissingSystemPromptAndEmptyBodyProducesDiagnostic()
    {
        const string content = "---\nname: ghost\ndescription: no prompt\n---\n";

        var result = AgentDefinitionParser.Parse(content, "/tmp/ghost.md", AgentSourceKind.Project);

        Assert.Null(result.Definition);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("missing_system_prompt", result.Diagnostic!.Code);
    }

    [Fact]
    public void ParseMapsThinkingLevelCaseInsensitively()
    {
        const string content = """
            ---
            name: thinker
            description: Thinks.
            thinkingLevel: xhigh
            ---

            body
            """;

        var definition = Assert.IsType<AgentDefinition>(AgentDefinitionParser.Parse(content, "/tmp/t.md", AgentSourceKind.Project).Definition);
        Assert.Equal(ThinkingLevel.XHigh, definition.ThinkingLevel);
    }

    [Fact]
    public void ParseSkillsAndAutoloadSkillsConflictProducesDiagnostic()
    {
        const string content = """
            ---
            name: conflicted
            description: Has both.
            skills: [a]
            autoloadSkills: [b]
            ---

            body
            """;

        var result = AgentDefinitionParser.Parse(content, "/tmp/c.md", AgentSourceKind.Project);

        var definition = Assert.IsType<AgentDefinition>(result.Definition);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal("skills_conflict", result.Diagnostic!.Code);
        Assert.Equal(["a"], definition.RestrictSkills);
        Assert.Equal(["b"], definition.AutoloadSkills);
    }

    [Fact]
    public void ParseBundledSetEnumeratesExpectedNames()
    {
        var bundled = BundledAgents.All;

        Assert.Equal(
            ["designer", "librarian", "reviewer", "scout", "security-reviewer", "sonic", "task"],
            bundled.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach (var definition in bundled.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.SystemPrompt), $"{definition.Name} needs a system prompt");
            Assert.False(string.IsNullOrWhiteSpace(definition.Description), $"{definition.Name} needs a description");
            Assert.Equal(AgentSourceKind.Bundled, definition.Source);
        }
        Assert.NotNull(bundled["reviewer"].OutputSchema);
    }
}
