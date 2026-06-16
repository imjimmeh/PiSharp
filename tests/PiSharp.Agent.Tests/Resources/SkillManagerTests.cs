using PiSharp.Agent.Resources;
using PiSharp.Agent.Tests;
using Xunit;

namespace PiSharp.Agent.Tests.Resources;

public sealed class SkillManagerTests
{
    [Fact]
    public async Task LoadAsyncLoadsRootMarkdownAndNestedSkillFilesWhenEnabled()
    {
        var fs = new FakeFileSystem();
        await fs.WriteFileAsync("/repo/skills/root.md", "---\ndescription: Root skill\n---\nRoot body");
        await fs.WriteFileAsync("/repo/skills/example/SKILL.md", "---\nname: example\ndescription: Example skill\n---\nExample body");

        var (skills, diagnostics) = await SkillManager.LoadAsync(fs, "/repo/skills", includeDirectMarkdownFiles: true);

        Assert.Empty(diagnostics);
        Assert.Contains(skills, skill => skill.Name == "root");
        Assert.Contains(skills, skill => skill.Name == "example");
    }

    [Fact]
    public async Task LoadAsyncIgnoresRootMarkdownFilesWhenDisabled()
    {
        var fs = new FakeFileSystem();
        await fs.WriteFileAsync("/repo/skills/root.md", "---\ndescription: Root skill\n---\nRoot body");
        await fs.WriteFileAsync("/repo/skills/example/SKILL.md", "---\nname: example\ndescription: Example skill\n---\nExample body");

        var (skills, diagnostics) = await SkillManager.LoadAsync(fs, "/repo/skills", includeDirectMarkdownFiles: false);

        Assert.Empty(diagnostics);
        Assert.DoesNotContain(skills, skill => skill.Name == "root");
        Assert.Contains(skills, skill => skill.Name == "example");
    }

    [Fact]
    public async Task LoadAsyncTreatsDirectorySkillFileAsBoundary()
    {
        var fs = new FakeFileSystem();
        await fs.WriteFileAsync("/repo/skills/example/SKILL.md", "---\ndescription: Parent skill\n---\nParent body");
        await fs.WriteFileAsync("/repo/skills/example/nested/SKILL.md", "---\ndescription: Nested skill\n---\nNested body");

        var (skills, _) = await SkillManager.LoadAsync(fs, "/repo/skills");

        var skill = Assert.Single(skills);
        Assert.Equal("example", skill.Name);
        Assert.Equal("Parent body", skill.Content.Trim());
    }

    [Fact]
    public async Task LoadAsyncSkipsSkillWithoutDescription()
    {
        var fs = new FakeFileSystem();
        await fs.WriteFileAsync("/repo/skills/example/SKILL.md", "---\nname: example\n---\nMissing description");

        var (skills, diagnostics) = await SkillManager.LoadAsync(fs, "/repo/skills");

        Assert.Empty(skills);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "missing_description" && diagnostic.Path.EndsWith("SKILL.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsyncHonorsDisableModelInvocationFrontmatter()
    {
        var fs = new FakeFileSystem();
        await fs.WriteFileAsync("/repo/skills/private/SKILL.md", "---\ndescription: Private skill\ndisable-model-invocation: true\n---\nPrivate body");

        var (skills, _) = await SkillManager.LoadAsync(fs, "/repo/skills");

        Assert.True(Assert.Single(skills).DisableModelInvocation);
    }

    [Fact]
    public void FormatInvocationIncludesNameAndLocation()
    {
        var skill = new Skill("test", "desc", "content", "/home/user/.agents/skills/test/SKILL.md");
        var invocation = SkillManager.FormatInvocation(skill);
        Assert.Contains("<skill name=\"test\"", invocation);
        Assert.Contains("SKILL.md", invocation);
    }
}
