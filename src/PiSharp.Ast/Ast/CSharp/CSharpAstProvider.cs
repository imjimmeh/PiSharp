using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PiSharp.Ast.Ast.CSharp;

/// <summary>
/// Roslyn-based C# provider: parses with <c>CSharpSyntaxTree</c> and delegates matching
/// (and, via <see cref="CSharpPatternRewriter"/>, rewriting) to the shared pattern engine.
/// </summary>
public sealed class CSharpAstProvider : IAstLanguageProvider
{
    public string Language => "csharp";

    public bool SupportsFile(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    public AstParseResult Parse(string source, string path)
    {
        var root = CSharpPatternParser.ParseSource(source, path);
        return new AstParseResult(root);
    }

    public IReadOnlyList<AstMatch> FindMatches(object root, string pattern, string sourceText, string path, int maxMatches)
    {
        var info = CSharpPatternParser.ParsePattern(pattern, path);
        return CSharpPatternMatcher.FindMatches(info, (SyntaxNode)root, sourceText, path, maxMatches);
    }

    public AstSearchResult ApplyRewrite(string sourceText, string pattern, string rewrite, string path)
    {
        return CSharpPatternRewriter.Rewrite(sourceText, pattern, rewrite, path, this);
    }
}
