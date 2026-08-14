using PiSharp.Ast.Ast;
using PiSharp.Ast.Ast.CSharp;
using Xunit;

namespace PiSharp.Ast.Tests;

public sealed class CSharpPatternMatcherTests
{
    private const string Source = """
        public class Sample
        {
            public int Add(int a, int b) => a + b;

            public void Calls()
            {
                var x = Add(1, 2);
                var y = Add(1, 2);
                var z = Add(3, 4);
                var c = Add(1, /* note */ 2);
                Other.Call(1, 2, 3);
                var same = a + a;
                var diff = a + b;
            }

            public void DoWork()
            {
                var value = 42;
            }
        }
        """;

    private static IReadOnlyList<AstMatch> Find(string pattern)
    {
        var info = CSharpPatternParser.ParsePattern(pattern, "test.cs");
        var root = CSharpPatternParser.ParseSource(Source, "test.cs");
        return CSharpPatternMatcher.FindMatches(info, root, Source, "test.cs", maxMatches: 1000);
    }

    [Fact]
    public void DollarNameCapturesExactlyOneNode()
    {
        var matches = Find("Add($A, $B)");
        Assert.Equal(4, matches.Count); // x, y, z, c
        Assert.All(matches, m =>
        {
            Assert.NotNull(m.Captures);
            Assert.True(m.Captures.ContainsKey("A"));
            Assert.True(m.Captures.ContainsKey("B"));
        });
        Assert.Equal("1", matches[0].Captures!["A"]);
        Assert.Equal("2", matches[0].Captures!["B"]);
        Assert.Equal("3", matches[2].Captures!["A"]);
        Assert.Equal("4", matches[2].Captures!["B"]);
    }

    [Fact]
    public void AnonymousWildcardMatchesButIsNotCaptured()
    {
        var matches = Find("Other.Call($_, $_, $_)");
        var match = Assert.Single(matches);
        Assert.Null(match.Captures);
    }

    [Fact]
    public void ListWildcardMatchesZeroOrMoreArguments()
    {
        var matches = Find("Add($$$ARGS)");
        Assert.Equal(4, matches.Count);
        Assert.Equal("1, 2", matches[0].Captures!["ARGS"]);
    }

    [Fact]
    public void AnonymousListWildcardMatchesAnyArguments()
    {
        var matches = Find("Other.Call($$$_)");
        var match = Assert.Single(matches);
        Assert.Equal("Other.Call(1, 2, 3)", match.Text);
        Assert.Null(match.Captures);
    }

    [Fact]
    public void RepeatedNameRequiresIdenticalText()
    {
        Assert.Single(Find("$A + $A"));        // a + a only
        Assert.Equal(3, Find("$A + $B").Count); // a+a, plus a+b twice (expression-bodied Add and Calls)
        Assert.Empty(Find("$A + $A + $A"));     // a + a has no third operand
    }

    [Fact]
    public void TriviaIsIgnored()
    {
        var matches = Find("Add($A, $B)");
        var comment = matches.Single(m => m.Captures!["B"] == "2" && m.Text.Contains("/* note */"));
        Assert.Equal("1", comment.Captures!["A"]);
    }

    [Fact]
    public void KindMismatchYieldsNoMatch()
    {
        Assert.Empty(Find("NonExistent($A)"));
        Assert.Empty(Find("Foo.Bar($A)"));
    }

    [Fact]
    public void LiteralTokenTextMustMatch()
    {
        var matches = Find("Add(1, $B)");
        Assert.Equal(3, matches.Count); // x, y, c — not z (3, 4)
        Assert.All(matches, m => Assert.Equal("2", m.Captures!["B"]));
    }

    [Fact]
    public void MethodPatternWithReturnTypeWildcardMatchesMethod()
    {
        var matches = Find("$_ DoWork($$$ARGS) { $$$BODY }");
        var match = Assert.Single(matches);
        Assert.Contains("DoWork", match.Text);
        Assert.Contains("var value = 42;", match.Captures!["BODY"]);
    }

    [Fact]
    public void MethodPatternMatchesWithModifiers()
    {
        var matches = Find("void Calls($$$ARGS) { $$$BODY }");
        var match = Assert.Single(matches);
        Assert.Contains("public void Calls", match.Text);
        Assert.Contains("var x = Add(1, 2);", match.Captures!["BODY"]);
    }

    [Fact]
    public void PatternParseErrorThrowsWithGuidance()
    {
        var ex = Assert.Throws<AstParseException>(() => Find("Foo($A); Bar($B);"));
        Assert.Contains("single node", ex.Message);
        Assert.Contains("wrap: `class $_ { … }`", ex.Message);
    }

    [Fact]
    public void MatchPositionsAreOneBased()
    {
        var matches = Find("Add(3, 4)");
        var match = Assert.Single(matches);
        Assert.True(match.Line >= 1);
        Assert.True(match.Column >= 1);
        Assert.True(match.EndLine >= match.Line);
        Assert.True(match.EndColumn > match.Column);
    }
}
