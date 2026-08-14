using PiSharp.Ast.Ast;
using PiSharp.Ast.Ast.CSharp;
using PiSharp.Ast.Tests.Fakes;
using PiSharp.Ast.Tools;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class AstGrepToolTests
{
    private const string Source = """
        public class Sample
        {
            public void Calls()
            {
                var x = Add(1, 2);
                var y = Add(3, 4);
            }
        }
        """;

    private static AstGrepTool NewTool(FakeExecutionEnv env, Func<bool>? enabled = null)
    {
        var registry = new AstLanguageRegistry();
        registry.Register(new CSharpAstProvider());
        return new AstGrepTool(env, registry, enabled);
    }

    [Fact]
    public async Task ReturnsMatchesWithPathLineColAndCaptures()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var result = await tool.ExecuteAsync("tc1", new AstGrepInput("Add($A, $B)"));

        Assert.Equal(2, result.Details!.Matches.Count);
        Assert.All(result.Details.Matches, m => Assert.Equal("/repo/sample.cs", m.Path));
        Assert.All(result.Details.Matches, m => Assert.True(m.Line >= 1));
        Assert.Equal("1", result.Details.Matches[0].Captures!["A"]);
        Assert.Equal("3", result.Details.Matches[1].Captures!["A"]);
        var text = result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text;
        Assert.Contains("sample.cs:", text);
        Assert.Contains("$A: 1", text);
    }

    [Fact]
    public async Task GlobFilterExcludesNonMatchingFiles()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("src/sub/a.cs", Source);
        env.AddFile("src/sub/b.cs", Source);
        var tool = NewTool(env);
        var result = await tool.ExecuteAsync("tc1", new AstGrepInput("Add($A, $B)", Path: "src", Glob: "**/b.cs"));
        Assert.Equal(2, result.Details!.Matches.Count);
        Assert.All(result.Details.Matches, m => Assert.Equal("/repo/src/sub/b.cs", m.Path));
    }

    [Fact]
    public async Task NonCSharpFileFailsWithUnsupportedLanguage()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("data.json", "{ }");
        var tool = NewTool(env);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.ExecuteAsync("tc1", new AstGrepInput("$A", Path: "data.json")));
        Assert.Contains("unsupported language", ex.Message);
        Assert.Contains("csharp", ex.Message);
    }

    [Fact]
    public async Task LimitCapsMatchesAndFlagsTruncation()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var result = await tool.ExecuteAsync("tc1", new AstGrepInput("Add($A, $B)", Limit: 1));
        Assert.Single(result.Details!.Matches);
        Assert.True(result.Details.MatchLimitReached);
    }

    [Fact]
    public async Task DisabledGateReturnsDisabledMessage()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env, enabled: () => false);
        var result = await tool.ExecuteAsync("tc1", new AstGrepInput("Add($A, $B)"));
        Assert.Contains("ast.enabled is false", result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text);
        Assert.Null(result.Details);
    }

    [Fact]
    public async Task InvalidPatternThrowsWithGuidance()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var ex = await Assert.ThrowsAsync<AstParseException>(
            () => tool.ExecuteAsync("tc1", new AstGrepInput("Foo($A); Bar($B);")));
        Assert.Contains("wrap: `class $_ { … }`", ex.Message);
    }
}
