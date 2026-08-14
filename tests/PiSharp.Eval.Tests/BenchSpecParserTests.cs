using PiSharp.Eval.Bench;
using PiSharp.Eval.Kernel.CSharp;
using PiSharp.Eval.Kernels;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// Bench spec parsing/validation: snake_case JSON deserialization and the validation
/// rules (name, runs ≥ 1, at least one case, unique names, single expected matcher,
/// known checker kernel).
/// </summary>
public sealed class BenchSpecParserTests
{
    public BenchSpecParserTests()
    {
        EvalKernelRegistry.Clear();
        EvalKernelRegistry.RegisterFactory(new CSharpKernelFactory());
    }

    private const string ValidSpec = """
        {
          "name": "smoke",
          "runs": 2,
          "cases": [
            { "name": "c1", "prompt": "Say hello", "expected": { "text": "hello" } }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidSpec()
    {
        var spec = BenchSpecParser.Parse(ValidSpec);

        Assert.Equal("smoke", spec.Name);
        Assert.Equal(2, spec.Runs);
        Assert.Single(spec.Cases);
        Assert.Equal("c1", spec.Cases[0].Name);
        Assert.Equal("hello", spec.Cases[0].Expected!.Text);
    }

    [Fact]
    public void Parse_Defaults()
    {
        var spec = BenchSpecParser.Parse("""
            { "name": "min", "cases": [ { "name": "c", "prompt": "p" } ] }
            """);

        Assert.Equal(1, spec.Runs);
        Assert.Null(spec.Model);
        Assert.Null(spec.Cases[0].Kernel);
    }

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse(""));
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("   "));
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("{ not json"));
    }

    [Fact]
    public void Validate_NameRequired()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("""
            { "cases": [ { "name": "c", "prompt": "p" } ] }
            """));
    }

    [Fact]
    public void Validate_RunsAtLeastOne()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("""
            { "name": "s", "runs": 0, "cases": [ { "name": "c", "prompt": "p" } ] }
            """));
    }

    [Fact]
    public void Validate_AtLeastOneCase()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse(""" { "name": "s" } """));
    }

    [Fact]
    public void Validate_CaseNameRequired()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("""
            { "name": "s", "cases": [ { "prompt": "p" } ] }
            """));
    }

    [Fact]
    public void Validate_DuplicateCaseNames()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("""
            {
              "name": "s",
              "cases": [
                { "name": "c", "prompt": "p1" },
                { "name": "c", "prompt": "p2" }
              ]
            }
            """));
    }

    [Fact]
    public void Validate_SingleExpectedMatcher()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("""
            {
              "name": "s",
              "cases": [
                { "name": "c", "prompt": "p", "expected": { "text": "a", "regex": "b" } }
              ]
            }
            """));
    }

    [Fact]
    public void Validate_UnknownCheckerKernel()
    {
        Assert.Throws<BenchSpecException>(() => BenchSpecParser.Parse("""
            {
              "name": "s",
              "cases": [
                { "name": "c", "prompt": "p", "checker": { "kernel": "brainfuck", "code": "x" } }
              ]
            }
            """));
    }
}
