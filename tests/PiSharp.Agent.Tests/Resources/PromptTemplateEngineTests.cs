using PiSharp.Agent.Resources;
using Xunit;

namespace PiSharp.Agent.Tests.Resources;

public sealed class PromptTemplateEngineTests
{
    [Theory]
    [InlineData("$1 ${@:2}", new[] { "hello", "test" }, "hello test")]
    [InlineData("$@", new[] { "a", "b" }, "a b")]
    [InlineData("$ARGUMENTS", new[] { "x", "y" }, "x y")]
    public void SubstituteArgsReplacesPlaceholders(string content, string[] args, string expected)
    {
        var result = PromptTemplateEngine.SubstituteArgs(content, args);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task PromptTemplateCatalogLoadsTemplatesFromDirectoriesAndFilesAndFormatsInvocations()
    {
        var env = new FakeFileSystem();
        await env.WriteFileAsync("/repo/first/release.md", "---\ndescription: Release notes\n---\nRelease $1 $ARGUMENTS");
        await env.WriteFileAsync("/repo/fix.md", "Fix ${@:1:2}");

        var (catalog, diagnostics) = await PromptTemplateCatalog.LoadAsync(env, ["/repo/first", "/repo/fix.md"]);

        Assert.Empty(diagnostics);
        Assert.Contains(catalog.Templates, template => template.Name == "release" && template.Description == "Release notes");
        Assert.Contains(catalog.Templates, template => template.Name == "fix");
        Assert.Equal("Release 1.2.3 1.2.3 stable", catalog.FormatInvocation("release", ["1.2.3", "stable"]));
        Assert.Equal("Fix a b", catalog.FormatInvocation("fix", ["a", "b", "c"]));
    }
}
