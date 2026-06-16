using PiSharp.Abstractions.Messages;
using PiSharp.Tools.Search;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class SearchAndLsToolTests
{
    [Fact]
    public async Task GrepToolFindsLiteralMatchesWithNativeFallbackSubset()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("src/a.txt", "alpha\nbeta\nAlpha");
        env.AddFile("src/b.cs", "beta");
        var tool = new GrepTool(env);
        var result = await tool.ExecuteAsync("tc1", new GrepToolInput("alpha", Path: "src", Glob: "*.txt", IgnoreCase: true, Literal: true));
        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Contains("a.txt:1:alpha", text);
        Assert.Contains("a.txt:3:Alpha", text);
        Assert.DoesNotContain("b.cs", text);
    }

    [Fact]
    public async Task FindToolMatchesGlobAndReportsLimit()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("src/a.cs", "");
        env.AddFile("src/b.cs", "");
        env.AddFile("src/c.txt", "");
        var tool = new FindTool(env);
        var result = await tool.ExecuteAsync("tc1", new FindToolInput("*.cs", Path: "src", Limit: 1));
        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Contains(".cs", text);
        Assert.Equal(1, result.Details?.ResultLimitReached);
    }

    [Fact]
    public async Task FindToolFallsBackToNativeWhenExternalSearchFailsAndIncludesDotfiles()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("src/.env", "secret");
        env.AddFile("src/app.txt", "visible");
        var tool = new FindTool(env);

        var result = await tool.ExecuteAsync("tc1", new FindToolInput("*", Path: "src"));

        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Contains(".env", text);
        Assert.Contains("app.txt", text);
    }

    [Fact]
    public async Task FindToolUsesExternalFdOutputWhenAvailable()
    {
        var env = new FakeExecutionEnv();
        env.EnqueueShellResult("src/a.cs\nsrc/b.cs\n");
        env.AddFile("src/native-only.cs", "");
        var tool = new FindTool(env);

        var result = await tool.ExecuteAsync("tc1", new FindToolInput("*.cs", Path: "src"));

        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Equal("src/a.cs\nsrc/b.cs", text);
        Assert.DoesNotContain("native-only", text);
        Assert.Null(result.Details?.ResultLimitReached);
    }

    [Fact]
    public async Task GrepToolNativeFallbackReportsRelativePathLineNumberContextAndTruncation()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("src/a.txt", "before\nneedle\nafter");
        env.AddFile("src/b.txt", new string('x', 4096) + " needle");
        var tool = new GrepTool(env);

        var result = await tool.ExecuteAsync("tc1", new GrepToolInput("needle", Path: "src", Glob: "*.txt", Literal: true, Context: 1, Limit: 3));

        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Contains("a.txt:1:before", text);
        Assert.Contains("a.txt:2:needle", text);
        Assert.Contains("a.txt:3:after", text);
        Assert.Equal(3, result.Details?.MatchLimitReached);
    }

    [Fact]
    public async Task GrepToolUsesExternalRipgrepOutputWhenAvailable()
    {
        var env = new FakeExecutionEnv();
        env.EnqueueShellResult("src/a.txt:1:needle\n");
        env.AddFile("src/native-only.txt", "needle");
        var tool = new GrepTool(env);

        var result = await tool.ExecuteAsync("tc1", new GrepToolInput("needle", Path: "src", Glob: "*.txt"));

        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Equal("src/a.txt:1:needle", text);
        Assert.DoesNotContain("native-only", text);
        Assert.Null(result.Details?.MatchLimitReached);
    }

    [Fact]
    public async Task GrepToolDoesNotFallbackToNativeSearchWhenRipgrepReportsNoMatches()
    {
        var env = new FakeExecutionEnv();
        env.EnqueueShellResult("", exitCode: 1);
        env.AddFile("node_modules/package/index.js", "needle");
        var tool = new GrepTool(env);

        var result = await tool.ExecuteAsync("tc1", new GrepToolInput("needle", Path: env.Cwd, Literal: true));

        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.Equal("No matches found", text);
    }

    [Fact]
    public async Task LsToolSortsDirectoriesFirstAndAppliesLimit()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("src/z.txt", "");
        env.AddFile("src/lib/a.txt", "");
        env.AddFile("src/a.txt", "");
        var tool = new LsTool(env);
        var result = await tool.ExecuteAsync("tc1", new LsToolInput("src", Limit: 2));
        var text = Assert.IsType<TextContent>(result.Content.Single()).Text;
        Assert.StartsWith("lib/\na.txt", text);
        Assert.Equal(2, result.Details?.EntryLimitReached);
    }

    [Fact]
    public void SearchSchemasUseTypescriptFieldNames()
    {
        var env = new FakeExecutionEnv();
        var grep = new GrepTool(env).ParametersSchema.GetRawText();
        Assert.Contains("pattern", grep);
        Assert.Contains("ignoreCase", grep);
        Assert.DoesNotContain("maxResults", grep);
        Assert.Contains("path", new FindTool(env).ParametersSchema.GetRawText());
        Assert.Contains("limit", new LsTool(env).ParametersSchema.GetRawText());
        Assert.DoesNotContain("longFormat", new LsTool(env).ParametersSchema.GetRawText());
    }
}
