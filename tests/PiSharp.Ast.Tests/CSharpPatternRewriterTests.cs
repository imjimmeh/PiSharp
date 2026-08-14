using PiSharp.Ast.Ast;
using PiSharp.Ast.Ast.CSharp;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class CSharpPatternRewriterTests
{
    private static readonly CSharpAstProvider Provider = new();

    private static AstSearchResult Rewrite(string source, string pattern, string rewrite)
        => Provider.ApplyRewrite(source, pattern, rewrite, "test.cs");

    [Fact]
    public void SingleCaptureSubstitutesVerbatim()
    {
        var result = Rewrite("class C { void M() { Foo(x); } }", "Foo($A)", "Bar($A)");
        Assert.Contains("Bar(x);", result.NewContent);
    }

    [Fact]
    public void ListCaptureSplicesSequence()
    {
        var result = Rewrite("class C { void M() { Foo(1, 2, 3); } }", "Foo($$$ARGS)", "Bar($$$ARGS)");
        Assert.Contains("Bar(1, 2, 3);", result.NewContent);
    }

    [Fact]
    public void MultipleMatchesRewrittenPerOp()
    {
        var result = Rewrite("class C { void M() { Foo(x); Foo(y); } }", "Foo($A)", "Bar($A)");
        Assert.Contains("Bar(x); Bar(y);", result.NewContent);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void UntouchedCodeIsByteIdentical()
    {
        const string source = "class C\n{\n    void M() { Foo(x); }\n}\n";
        var result = Rewrite(source, "Foo($A)", "Bar($A)");
        Assert.Contains("class C\n{\n    void M() { Bar(x); }\n}\n", result.NewContent);
    }

    [Fact]
    public void MalformedRewriteThrows()
    {
        var ex = Assert.Throws<AstParseException>(() => Rewrite("class C { void M() { Foo(x); } }", "Foo($A)", "Bar($A"));
        Assert.Contains("wrap: `class $_ { … }`", ex.Message);
    }

    [Fact]
    public void NoOpRewriteIsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Rewrite("class C { void M() { Foo(x); } }", "Foo($A)", "Foo($A)"));
        Assert.Contains("no changes", ex.Message);
    }

    [Fact]
    public void ZeroMatchesIsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Rewrite("class C { void M() { Foo(x); } }", "Bar($A)", "Baz($A)"));
        Assert.Contains("did not match", ex.Message);
    }

    [Fact]
    public void AnonymousWildcardInRewriteIsRejected()
    {
        var ex = Assert.Throws<AstParseException>(() => Rewrite("class C { void M() { Foo(x); } }", "Foo($A)", "Bar($_)"));
        Assert.Contains("anonymous", ex.Message);
    }

    [Fact]
    public void OverlappingMatchesRejectedWithinOp()
    {
        // `Bar(Bar(x))` matches the outer and the inner invocation — overlapping spans.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Rewrite("class C { void M() { Bar(Bar(x)); } }", "Bar($$$ARGS)", "Baz($$$ARGS)"));
        Assert.Contains("overlapping", ex.Message);
    }

    [Fact]
    public void NonOverlappingMultiMatchSucceeds()
    {
        var result = Rewrite("class C { void M() { var x = a + b; } }", "$A + $B", "$B + $A");
        Assert.Contains("b + a", result.NewContent);
    }
}
