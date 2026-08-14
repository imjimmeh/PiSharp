using Xunit;

namespace PiSharp.Extensions.Rules.Tests;

public sealed class RuleFrontmatterTests
{
    [Fact]
    public void NoFrontmatter_UsesDefaultsAndWholeFileAsBody()
    {
        var parsed = RuleFrontmatter.Parse("Just markdown content,\nover multiple lines.", "/repo/rules/no-todo.md");

        Assert.Equal("no-todo", parsed.Name);
        Assert.False(parsed.Always);
        Assert.Null(parsed.Pattern);
        Assert.Equal(0, parsed.Priority);
        Assert.Equal("Just markdown content,\nover multiple lines.", parsed.Content);
    }

    [Fact]
    public void Frontmatter_ParsesAlwaysPatternPriorityAndName()
    {
        const string text = """
            ---
            name: no-todo
            always: true
            pattern: (?i)todo list
            priority: 5
            ---
            Body of the rule.
            """;

        var parsed = RuleFrontmatter.Parse(text, "/repo/rules/no-todo.md");

        Assert.Equal("no-todo", parsed.Name);
        Assert.True(parsed.Always);
        Assert.Equal("(?i)todo list", parsed.Pattern);
        Assert.Equal(5, parsed.Priority);
        Assert.Equal("Body of the rule.", parsed.Content);
    }

    [Fact]
    public void Frontmatter_DefaultsNameToStemWhenOmitted()
    {
        const string text = """
            ---
            always: true
            ---
            Body.
            """;

        var parsed = RuleFrontmatter.Parse(text, "/repo/rules/stem-name.md");

        Assert.Equal("stem-name", parsed.Name);
        Assert.True(parsed.Always);
    }

    [Fact]
    public void UnclosedFrontmatterMarker_TreatedAsBody()
    {
        var parsed = RuleFrontmatter.Parse("---\nname: x\nno closing marker", "/repo/rules/odd.md");

        // Note: the frontmatter delimiter never closes, so the whole text is the body.
        Assert.Equal("odd", parsed.Name);
        Assert.False(parsed.Always);
        Assert.Contains("---", parsed.Content);
    }

    [Fact]
    public void Frontmatter_MalformedYaml_ReturnsDefaultsAndBody()
    {
        const string text = "---\nname: [unclosed\n---\nBody.";
        var parsed = RuleFrontmatter.Parse(text, "/repo/rules/malformed.md");

        Assert.Equal("malformed", parsed.Name);
        Assert.Equal("Body.", parsed.Content);
    }
}
