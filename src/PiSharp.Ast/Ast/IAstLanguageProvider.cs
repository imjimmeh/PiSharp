namespace PiSharp.Ast.Ast;

/// <summary>
/// One structural match: the source span, matched text, and per-metavariable captures
/// (anonymous <c>$_</c>/<c>$$$_</c> captures are excluded).
/// </summary>
public sealed record AstMatch(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Text,
    IReadOnlyDictionary<string, string>? Captures = null);

/// <summary>Result of a structural search/rewrite across one source file.</summary>
public sealed record AstSearchResult(IReadOnlyList<AstMatch> Matches, string NewContent);

/// <summary>
/// Language-provider seam for the structural tools. Implementations own parsing,
/// pattern matching and rewriting for one language; the tools never carry
/// language-specific logic.
/// </summary>
public interface IAstLanguageProvider
{
    /// <summary>Canonical language id, e.g. "csharp".</summary>
    string Language { get; }

    /// <summary>Extension-based detection, e.g. ".cs" → true.</summary>
    bool SupportsFile(string path);

    /// <summary>
    /// Parses a source file. Throws <see cref="AstParseException"/> when the source has
    /// syntax errors or is not parseable.
    /// </summary>
    AstParseResult Parse(string source, string path);

    /// <summary>
    /// Finds all non-overlapping (per the EditDiff overlap rule) structural matches of
    /// <paramref name="pattern"/> in the given parse root. Returns matches ordered by span.
    /// </summary>
    IReadOnlyList<AstMatch> FindMatches(object root, string pattern, string sourceText, string path, int maxMatches);

    /// <summary>
    /// Applies a structural rewrite to <paramref name="sourceText"/>. Returns the new
    /// content and the matches that were rewritten. Throws <see cref="AstParseException"/>
    /// for malformed patterns/rewrites and <see cref="InvalidOperationException"/> for
    /// overlapping matches or no-op rewrites.
    /// </summary>
    AstSearchResult ApplyRewrite(string sourceText, string pattern, string rewrite, string path);
}

/// <summary>Parse result; <see cref="Root"/> is the provider's own node type.</summary>
public sealed record AstParseResult(object Root);

/// <summary>Raised for unparseable patterns, rewrites, or source files.</summary>
public sealed class AstParseException(string message) : Exception(message);
