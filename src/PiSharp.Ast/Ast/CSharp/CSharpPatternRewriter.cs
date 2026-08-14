using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PiSharp.Ast.Ast.CSharp;

/// <summary>
/// Applies a structural rewrite: finds every non-overlapping match of <paramref name="pattern"/>
/// and replaces each matched span with <paramref name="rewrite"/> where metavariables are
/// substituted verbatim with their captured text. Only replaced subtrees are normalized
/// (<c>NormalizeWhitespace</c>); untouched code stays byte-identical. A malformed rewrite, an
/// overlapping match set, a zero-match op, or a rewrite that produces identical text are all
/// rejected before any output is produced.
/// </summary>
public static class CSharpPatternRewriter
{
    private static readonly Regex MetavarToken =
        new(@"\$(?:\$\$)?([A-Z_][A-Z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AstSearchResult Rewrite(string sourceText, string pattern, string rewrite, string path, IAstLanguageProvider provider)
    {
        var patternInfo = CSharpPatternParser.ParsePattern(pattern, path);
        var sourceRoot = CSharpPatternParser.ParseSource(sourceText, path);

        var rewriteInfo = CSharpPatternParser.ParsePattern(rewrite, path);
        RejectAnonymousWildcardsInRewrite(rewriteInfo, path);

        var matches = CSharpPatternMatcher.FindMatches(patternInfo, sourceRoot, sourceText, path, int.MaxValue);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"{path}: pattern '{pattern}' did not match any nodes.");
        }


        var ordered = matches.OrderBy(m => m.Line * 1_000_000L + m.Column)
            .ThenBy(m => m.EndLine * 1_000_000L + m.EndColumn).ToList();
        // Recompute byte offsets (AstMatch carries line:col, not offset), then overlap-check in byte order.
        var offsets = ResolveOffsets(sourceText, ordered);

        // Overlap rejection: two matches that share a byte span.
        for (var i = 1; i < offsets.Count; i++)
        {
            if (offsets[i].Start < offsets[i - 1].End)
            {
                throw new InvalidOperationException(
                    $"{path}: overlapping matches for pattern '{pattern}' cannot be rewritten in one op.");
            }
        }

        var substitutions = new List<(int Start, int End, string Text)>(offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            var replacement = BuildReplacement(rewrite, ordered[i].Captures, path);
            substitutions.Add((offsets[i].Start, offsets[i].End, replacement));
        }

        var newContent = ApplyFromEnd(sourceText, substitutions);
        if (newContent == sourceText)
        {
            throw new InvalidOperationException($"{path}: rewrite '{rewrite}' produced no changes.");
        }
        return new AstSearchResult(ordered, newContent);
    }


    private static void RejectAnonymousWildcardsInRewrite(CSharpPatternInfo rewriteInfo, string path)
    {
        if (rewriteInfo.Metavariables.Any(m => m.Name == "_"))
        {
            throw new AstParseException($"{path}: rewrite uses the anonymous wildcard $_; anonymous metavariables cannot be the target of a rewrite.");
        }
    }

    /// <summary>Substitutes metavariables into the rewrite template and normalizes the result.</summary>
    private static string BuildReplacement(string rewrite, IReadOnlyDictionary<string, string>? captures, string path)
    {
        var substituted = MetavarToken.Replace(rewrite, match =>
        {
            var name = match.Groups[1].Value;
            var isAnonymous = name == "_";
            if (isAnonymous)
            {
                // Reached only if the rewrite's anonymous wildcard survived a non-referenced branch.
                return match.Value;
            }
            if (captures is null || !captures.TryGetValue(name, out var text))
            {
                throw new InvalidOperationException(
                    $"{path}: rewrite references metavariable ${name} which was not captured by the pattern.");
            }
            return text;
        });

        // Validate + normalize the substituted result as a single node. ParsePattern applies the
        // same repair ladder as patterns, so expression rewrites like `Bar($A)` (which need a `;`
        // to parse at top level) work; the extracted target drops the scaffold.
        CSharpPatternInfo parsed;
        try
        {
            parsed = CSharpPatternParser.ParsePattern(substituted, path);
        }
        catch (AstParseException ex)
        {
            throw new AstParseException(
                $"{path}: rewrite '{rewrite}' is malformed: {ex.Message}. wrap: `class $_ {{ … }}`");
        }
        var target = parsed.Target;
        return target.IsToken
            ? target.AsToken().Text
            : target.AsNode()!.NormalizeWhitespace().ToFullString();
    }

    private static List<(int Start, int End)> ResolveOffsets(string sourceText, List<AstMatch> matches)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < sourceText.Length; i++)
        {
            if (sourceText[i] == '\n') lineStarts.Add(i + 1);
        }

        var offsets = new List<(int Start, int End)>(matches.Count);
        foreach (var match in matches)
        {
            var start = lineStarts[match.Line - 1] + (match.Column - 1);
            var end = lineStarts[match.EndLine - 1] + (match.EndColumn - 1);
            offsets.Add((start, end));
        }
        return offsets;
    }

    private static string ApplyFromEnd(string sourceText, List<(int Start, int End, string Text)> substitutions)
    {
        var sb = new System.Text.StringBuilder(sourceText);
        foreach (var (start, end, text) in substitutions.OrderByDescending(s => s.Start))
        {
            sb.Remove(start, end - start);
            sb.Insert(start, text);
        }
        return sb.ToString();
    }
}
