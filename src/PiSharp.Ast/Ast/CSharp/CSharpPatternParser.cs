using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PiSharp.Ast.Ast.CSharp;

/// <summary>One metavariable found in a pattern/rewrite text.</summary>
public sealed record MetavariableInfo(string Name, bool IsList, int Occurrences);

/// <summary>A parsed structural pattern: the single-node target plus its metavariables.</summary>
public sealed record CSharpPatternInfo(SyntaxNodeOrToken Target, IReadOnlyList<MetavariableInfo> Metavariables, string MaskedText);

/// <summary>
/// Parses structural patterns and rewrites. Metavariables (<c>$A</c>, <c>$_</c>, <c>$$$ARGS</c>,
/// <c>$$$_</c>) are masked into synthetic identifiers before Roslyn parsing because <c>$</c> is
/// not a legal C# identifier character; a repair pass turns list placeholders in parameter
/// positions into typed parameters (<c>int __psh_l_P</c>) and statement positions into
/// terminated statements (<c>__psh_l_BODY;</c>) so the canonical forms
/// <c>void Foo($$$ARGS) { }</c> and <c>{ $$$BODY }</c> parse.
/// </summary>
public static class CSharpPatternParser
{
    internal const string SinglePrefix = "__psh_s_";
    internal const string ListPrefix = "__psh_l_";

    /// <summary>
    /// Masks metavariable tokens with placeholder identifiers. Anything else containing <c>$</c>
    /// (e.g. interpolation strings) is left as literal text.
    /// </summary>
    public static string Mask(string patternText)
    {
        var sb = new StringBuilder(patternText.Length);
        for (var i = 0; i < patternText.Length; i++)
        {
            if (patternText[i] != '$')
            {
                sb.Append(patternText[i]);
                continue;
            }

            // List form requires exactly "$$$" followed by a name.
            var isList = i + 2 < patternText.Length && patternText[i + 1] == '$' && patternText[i + 2] == '$';
            var nameStart = isList ? i + 3 : i + 1;
            var nameEnd = nameStart;
            if (nameStart < patternText.Length && (char.IsUpper(patternText[nameStart]) || patternText[nameStart] == '_'))
            {
                nameEnd = nameStart + 1;
                while (nameEnd < patternText.Length &&
                       (char.IsUpper(patternText[nameEnd]) || char.IsDigit(patternText[nameEnd]) || patternText[nameEnd] == '_'))
                {
                    nameEnd++;
                }
            }

            if (nameEnd > nameStart)
            {
                sb.Append(isList ? ListPrefix : SinglePrefix).Append(patternText, nameStart, nameEnd - nameStart);
                i = nameEnd - 1;
            }
            else
            {
                sb.Append('$');
            }
        }
        return sb.ToString();
    }

    /// <summary>Parses a source file; throws <see cref="AstParseException"/> on syntax errors.</summary>
    public static SyntaxNode ParseSource(string sourceText, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new AstParseException($"{path}: source has syntax errors: {errors[0].GetMessage()}");
        }
        return tree.GetRoot();
    }

    /// <summary>
    /// Parses a pattern (or rewrite template). The masked text must parse to exactly one
    /// top-level node; multi-node or unparseable input is rejected with single-node guidance.
    /// </summary>
    public static CSharpPatternInfo ParsePattern(string patternText, string path)
    {
        var masked = Mask(patternText);
        var (root, repairedMasked) = ParseWithRepair(masked);
        if (root is null)
        {
            var firstError = FirstErrorText(repairedMasked);
            throw new AstParseException(
                $"{path}: pattern does not parse as a single node: {firstError}. wrap: `class $_ {{ … }}`");
        }

        var target = ExtractTarget(root, path);
        return new CSharpPatternInfo(target, CollectMetavariables(masked), repairedMasked);
    }

    /// <summary>
    /// Extracts the single meaningful node from a parsed compilation unit: member declarations
    /// stay whole; <c>GlobalStatement</c> wrappers unwrap so expression patterns match anywhere.
    /// </summary>
    public static SyntaxNodeOrToken ExtractTarget(SyntaxNode root, string path)
    {
        var members = root.ChildNodes().ToList();
        if (members.Count != 1)
        {
            throw new AstParseException(
                $"{path}: pattern must parse as a single node (found {members.Count}). wrap: `class $_ {{ … }}`");
        }

        var child = members[0];
        if (child is GlobalStatementSyntax globalStatement)
        {
            var statement = globalStatement.Statement;
            if (statement is ExpressionStatementSyntax expressionStatement)
            {
                return expressionStatement.Expression;
            }
            return statement;
        }
        return child;
    }

    /// <summary>Collects metavariables from already-masked text.</summary>
    public static IReadOnlyList<MetavariableInfo> CollectMetavariables(string maskedText)
    {
        var counts = new Dictionary<string, (bool IsList, int Count)>(StringComparer.Ordinal);
        foreach (var token in CSharpSyntaxTree.ParseText(maskedText).GetRoot().DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;
            if (ParsePlaceholder(token.Text) is not { } info || info.Name is null) continue;
            counts.TryGetValue(info.Name, out var existing);
            counts[info.Name] = (info.IsList, existing.Count + 1);
        }
        return counts.Select(pair => new MetavariableInfo(pair.Key, pair.Value.IsList, pair.Value.Count)).ToArray();
    }

    internal static (string? Name, bool IsList)? ParsePlaceholder(string text)
    {
        if (text.StartsWith(ListPrefix, StringComparison.Ordinal)) return (text[ListPrefix.Length..], true);
        if (text.StartsWith(SinglePrefix, StringComparison.Ordinal)) return (text[SinglePrefix.Length..], false);
        return null;
    }

    internal static bool IsPlaceholder(string text) => text.StartsWith(SinglePrefix, StringComparison.Ordinal) || text.StartsWith(ListPrefix, StringComparison.Ordinal);

    private static (SyntaxNode? Root, string Masked) ParseWithRepair(string masked)
    {
        var current = masked;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var tree = CSharpSyntaxTree.ParseText(current);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length == 0) return (tree.GetRoot(), current);

            var repaired = TryRepair(current, tree.GetRoot(), errors);
            if (repaired is null || repaired == current) return (null, current);
            current = repaired;
        }
        return (null, current);
    }

    private static string? TryRepair(string masked, SyntaxNode root, IReadOnlyList<Diagnostic> errors)
    {
        var error = errors[0];
        var position = error.Location.SourceSpan.Start;
        var tokens = root.DescendantTokens().ToArray();

        // Nearest placeholder token ending at or before the error position (Roslyn reports
        // "identifier expected" AFTER the token it consumed as a type, and "; expected" at the
        // next delimiter).
        SyntaxToken? nearest = null;
        foreach (var token in tokens)
        {
            if (token.IsKind(SyntaxKind.IdentifierToken) && IsPlaceholder(token.Text) && token.Span.End <= position)
            {
                nearest = token;
            }
        }

        if (error.Id == "CS1001" && nearest is { } type)
        {
            // A list placeholder in parameter position needs a type scaffold: `int __psh_l_P`.
            return masked[..type.SpanStart] + "int " + masked[type.SpanStart..];
        }

        if (error.Id is "CS1002" or "CS1003")
        {
            // A statement position needs its terminating semicolon, inserted where Roslyn says
            // `;` is expected (before `}` or at end of input). No placeholder needed — plain
            // expression patterns like `Add(3, 4)` rely on this. The attempt cap bounds loops.
            var at = Math.Min(position, masked.Length);
            return masked[..at] + ";" + masked[at..];
        }
        return null;
    }

    private static string FirstErrorText(string masked)
    {
        var tree = CSharpSyntaxTree.ParseText(masked);
        var error = tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        return error is null ? "parse failed" : error.GetMessage();
    }
}
