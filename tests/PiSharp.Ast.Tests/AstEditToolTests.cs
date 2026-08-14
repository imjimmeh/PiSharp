using PiSharp.Ast.Ast;
using PiSharp.Ast.Ast.CSharp;
using PiSharp.Ast.Tests.Fakes;
using PiSharp.Ast.Tools;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class AstEditToolTests
{
    private const string Source = """
        public class Sample
        {
            public void Calls()
            {
                Foo(a);
                Foo(b);
            }
        }
        """;

    private static AstEditTool NewTool(FakeExecutionEnv env, Func<bool>? enabled = null)
    {
        var registry = new AstLanguageRegistry();
        registry.Register(new CSharpAstProvider());
        return new AstEditTool(env, registry, enabled);
    }

    private static AstEditOp RenameOp(string from = "Foo($A)", string to = "Bar($A)") => new(from, to);

    [Fact]
    public async Task ProposalWritesNothingAndReturnsHash()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var result = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()], Apply: false));

        Assert.False(result.Details!.Applied);
        Assert.NotNull(result.Details.ContentHash);
        Assert.Equal(2, result.Details.Proposals[0].MatchCount);
        Assert.Contains("Bar", result.Details.Proposals[0].PreviewDiff);
        Assert.Equal(Source, env.ReadFileOrDefault("sample.cs")); // zero writes
    }

    [Fact]
    public async Task ApplyWithExpectedHashWritesOnce()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var proposal = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()], Apply: false));
        var hash = proposal.Details!.ContentHash!;

        var applied = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()], Apply: true, ExpectedHash: hash));
        Assert.True(applied.Details!.Applied);
        Assert.Contains("Bar(a);", env.ReadFileOrDefault("sample.cs"));
        Assert.Contains("Bar(b);", env.ReadFileOrDefault("sample.cs"));
        Assert.NotNull(applied.Details.Diff);
        Assert.True(applied.Details.FirstChangedLine >= 1);
    }

    [Fact]
    public async Task ApplyWithStaleHashRejectsBeforeAnyWrite()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var proposal = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()], Apply: false));

        env.AddFile("sample.cs", Source + "\n// mutated after proposal\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()], Apply: true, ExpectedHash: proposal.Details!.ContentHash)));
        Assert.Contains("Stale proposal", ex.Message);
        Assert.Contains("// mutated after proposal", env.ReadFileOrDefault("sample.cs")); // zero writes
    }

    [Fact]
    public async Task ApplyWithoutExpectedHashRecomputesOnFreshContent()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var applied = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()], Apply: true));
        Assert.True(applied.Details!.Applied);
        Assert.Contains("Bar(a);", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task SequentialOpsCompose()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var ops = new[] { RenameOp(), new AstEditOp("Bar($A)", "Baz($A)") };
        var proposal = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", ops, Apply: false));
        Assert.Equal(2, proposal.Details!.Proposals.Count);
        Assert.Equal(2, proposal.Details.Proposals[1].MatchCount); // op2 sees op1's output

        var applied = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", ops, Apply: true, ExpectedHash: proposal.Details.ContentHash));
        Assert.True(applied.Details!.Applied);
        Assert.Contains("Baz(a);", env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task MalformedRewriteFailsWithoutWriting()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env);
        var ex = await Assert.ThrowsAsync<AstParseException>(
            () => tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [new AstEditOp("Foo($A)", "Bar($A")])));
        Assert.Contains("wrap: `class $_ { … }`", ex.Message);
        Assert.Equal(Source, env.ReadFileOrDefault("sample.cs"));
    }

    [Fact]
    public async Task NonCSharpFileFailsWithUnsupportedLanguage()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("data.json", "{}");
        var tool = NewTool(env);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.ExecuteAsync("tc1", new AstEditInput("data.json", [RenameOp()])));
        Assert.Contains("unsupported language", ex.Message);
    }

    [Fact]
    public async Task DisabledGateReturnsDisabledMessage()
    {
        var env = new FakeExecutionEnv();
        env.AddFile("sample.cs", Source);
        var tool = NewTool(env, enabled: () => false);
        var result = await tool.ExecuteAsync("tc1", new AstEditInput("sample.cs", [RenameOp()]));
        Assert.Contains("ast.enabled is false", result.Content.OfType<PiSharp.Abstractions.Messages.TextContent>().Single().Text);
    }
}
