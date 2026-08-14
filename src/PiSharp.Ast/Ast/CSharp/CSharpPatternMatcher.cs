using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PiSharp.Ast.Ast.CSharp;

/// <summary>
/// Structural pattern matcher over a Roslyn C# tree (§4.2 of the P30 plan):
/// <list type="bullet">
/// <item><c>$A</c> captures exactly one source node/token; repeating the same name requires
/// identical captured text.</item>
/// <item><c>$_</c> is an anonymous single wildcard (not captured).</item>
/// <item><c>$$$NAME</c> / <c>$$$_</c> absorb a run of sibling nodes in a list position
/// (arguments, parameters, statements), zero or more.</item>
/// <item>Trivia is ignored; identifier/literal token text must match unless the token is a
/// metavariable.</item>
/// </list>
/// Children align 1:1 positionally (a list wildcard absorbs a contiguous run), with leading
/// source children that are modifier tokens/attribute-lists tolerated so a pattern may omit
/// modifiers. Captures keep the verbatim source text (list captures include separators).
/// </summary>
public static class CSharpPatternMatcher
{
    public static IReadOnlyList<AstMatch> FindMatches(
        CSharpPatternInfo pattern, SyntaxNode sourceRoot, string sourceText, string path, int maxMatches)
    {
        var target = pattern.Target;
        var lineStarts = BuildLineStarts(sourceText);
        var matches = new List<AstMatch>();

        if (target.IsToken)
        {
            foreach (var token in sourceRoot.DescendantTokens())
            {
                if (matches.Count >= maxMatches) break;
                var caps = TryMatchToken(target.AsToken(), token, new Dictionary<string, string>(StringComparer.Ordinal));
                if (caps is not null)
                {
                    matches.Add(MakeMatch(sourceText, lineStarts, path, token.Span, caps));
                }
            }
            return matches;
        }

        foreach (var node in sourceRoot.DescendantNodesAndSelf())
        {
            if (matches.Count >= maxMatches) break;
            var caps = TryMatchNode(target.AsNode()!, node,
                new Dictionary<string, string>(StringComparer.Ordinal), sourceText);
            if (caps is not null)
            {
                matches.Add(MakeMatch(sourceText, lineStarts, path, node.Span, caps));
            }
        }
        return matches;
    }

    private static AstMatch MakeMatch(string sourceText, IReadOnlyList<int> lineStarts, string path, TextSpan span, Dictionary<string, string> captures)
    {
        GetLocation(lineStarts, span, out var line, out var col, out var endLine, out var endCol);
        var text = sourceText.Substring(span.Start, span.Length);
        return new AstMatch(path, line, col, endLine, endCol, text,
            captures.Count == 0 ? null : new Dictionary<string, string>(captures, StringComparer.Ordinal));
    }

    // ---- Matching ----

    private static Dictionary<string, string>? TryMatchNode(
        SyntaxNode p, SyntaxNode s, Dictionary<string, string> captures, string sourceText)
    {
        if (IsSingleWildcardNode(p))
        {
            return CaptureSingle(PlaceholderName(p), s.ToString(), captures);
        }
        if (!p.IsKind(s.Kind()))
        {
            // A bare `void Foo($$$ARGS) { $$$BODY }` pattern parses as a local function at top
            // level; allow it to match method declarations (plan §4.2 method-pattern tolerance).
            if (!(p is LocalFunctionStatementSyntax && s is MethodDeclarationSyntax))
            {
                return null;
            }
        }
        return MatchChildren(p.ChildNodesAndTokens(), 0, s.ChildNodesAndTokens(), 0, captures, sourceText, allowSkip: true);
    }

    private static Dictionary<string, string>? TryMatchToken(SyntaxToken p, SyntaxToken s, Dictionary<string, string> captures)
    {
        if (IsSinglePlaceholderToken(p))
        {
            return CaptureSingle(PlaceholderNameFromToken(p), s.Text, captures);
        }
        return p.IsKind(s.Kind()) && p.Text == s.Text ? captures : null;
    }

    private static Dictionary<string, string>? MatchChildren(
        ChildSyntaxList pKids, int pi, ChildSyntaxList sKids, int si, Dictionary<string, string> captures, string sourceText, bool allowSkip)
    {
        if (pi == pKids.Count)
        {
            // All pattern children consumed; no source children may remain.
            return si == sKids.Count ? captures : null;
        }

        if (allowSkip && pi == 0 && si < sKids.Count && IsToleratedLeadingChild(sKids[si]))
        {
            // Skip a tolerated leading source child (modifier token / attribute list).
            return MatchChildren(pKids, pi, sKids, si + 1, captures, sourceText, true);
        }

        var p = pKids[pi];

        if (IsListWildcard(p))
        {
            // Absorb a contiguous run of source children [si..k), greedy-largest so the deepest
            // match wins, backtracking so later pattern children can still align.
            var name = ListPlaceholderName(p);
            for (var k = sKids.Count; k >= si; k--)
            {
                var working = captures;
                if (k > si)
                {
                    var text = SliceRun(sourceText, sKids, si, k);
                    if (name != "_")
                    {
                        if (working.TryGetValue(name, out var existing) && existing != text)
                        {
                            continue;
                        }
                        working = new Dictionary<string, string>(working, StringComparer.Ordinal) { [name] = text };
                    }
                }
                var rest = MatchChildren(pKids, pi + 1, sKids, k, working, sourceText, false);
                if (rest is not null) return rest;
            }
            return null;
        }

        // 1:1 positional pair for a non-wildcard pattern child.
        if (si >= sKids.Count) return null;
        var matched = p.IsToken
            ? TryMatchToken(p.AsToken(), sKids[si].AsToken(), captures)
            : TryMatchNode(p.AsNode()!, sKids[si].AsNode()!, captures, sourceText);
        if (matched is null) return null;
        return MatchChildren(pKids, pi + 1, sKids, si + 1, matched, sourceText, false);
    }

    // ---- Predicates ----

    /// <summary>A node that is solely a single placeholder, e.g. IdentifierName($A).</summary>
    private static bool IsSingleWildcardNode(SyntaxNode node)
    {
        var kids = node.ChildNodesAndTokens();
        return kids.Count == 1 && kids[0].IsToken && IsSinglePlaceholderToken(kids[0].AsToken());
    }

    private static bool IsSinglePlaceholderToken(SyntaxToken token)
        => token.IsKind(SyntaxKind.IdentifierToken)
           && CSharpPatternParser.ParsePlaceholder(token.Text) is { } info && !info.IsList;

    /// <summary>
    /// A node/token that is a list wildcard: a <c>$$$NAME</c> marker, possibly wrapped by a
    /// scaffold (e.g. the <c>int</c> the repair inserts for a parameter, or a statement's
    /// terminating <c>;</c>). List-container nodes themselves (ArgumentList, ParameterList, ...)
    /// are never wildcards — their children carry the run semantics. A node containing any other
    /// identifier is structural, not a wildcard.
    /// </summary>
    private static bool IsListWildcard(SyntaxNodeOrToken atom)
    {
        if (atom.IsToken)
        {
            return CSharpPatternParser.ParsePlaceholder(atom.AsToken().Text) is { } info && info.IsList;
        }
        var node = atom.AsNode()!;
        if (IsListContainer(node))
        {
            return false;
        }
        var hasListPlaceholder = false;
        foreach (var token in node.DescendantTokens())
        {
            var info = CSharpPatternParser.ParsePlaceholder(token.Text);
            if (info is { IsList: true })
            {
                hasListPlaceholder = true;
                continue;
            }
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                return false; // real identifier → structural node, not a wildcard
            }
        }
        return hasListPlaceholder;
    }

    private static bool IsListContainer(SyntaxNode node) =>
        node is ArgumentListSyntax
            or ParameterListSyntax
            or BracketedArgumentListSyntax
            or TypeArgumentListSyntax
            or AttributeArgumentListSyntax;

    /// <summary>
    /// Children a source node may have that a pattern is allowed to omit (modifier keywords,
    /// attribute lists). Allows <c>void Foo(...) { }</c> to match <c>public void Foo(...) { }</c>.
    /// </summary>
    private static bool IsToleratedLeadingChild(SyntaxNodeOrToken child)
    {
        if (child.IsToken)
        {
            var kind = child.AsToken().Kind();
            return kind is SyntaxKind.PublicKeyword
                or SyntaxKind.PrivateKeyword
                or SyntaxKind.InternalKeyword
                or SyntaxKind.ProtectedKeyword
                or SyntaxKind.StaticKeyword
                or SyntaxKind.SealedKeyword
                or SyntaxKind.AbstractKeyword
                or SyntaxKind.VirtualKeyword
                or SyntaxKind.OverrideKeyword
                or SyntaxKind.ReadOnlyKeyword
                or SyntaxKind.AsyncKeyword
                or SyntaxKind.VolatileKeyword
                or SyntaxKind.PartialKeyword
                or SyntaxKind.ExternKeyword
                or SyntaxKind.NewKeyword;
        }
        return child.AsNode() is AttributeListSyntax;
    }

    private static string PlaceholderName(SyntaxNode node)
        => PlaceholderNameFromToken(node.ChildNodesAndTokens()[0].AsToken());

    private static string PlaceholderNameFromToken(SyntaxToken token)
    {
        var info = CSharpPatternParser.ParsePlaceholder(token.Text);
        return info?.Name ?? token.Text;
    }

    private static string ListPlaceholderName(SyntaxNodeOrToken atom)
    {
        if (atom.IsToken)
        {
            var info = CSharpPatternParser.ParsePlaceholder(atom.AsToken().Text);
            return info is not null && info.Value.IsList ? info.Value.Name ?? "" : "";
        }
        foreach (var token in atom.AsNode()!.DescendantTokens())
        {
            var info = CSharpPatternParser.ParsePlaceholder(token.Text);
            if (info is not null && info.Value.IsList)
            {
                return info.Value.Name ?? "";
            }
        }
        return "";
    }

    private static Dictionary<string, string> CaptureSingle(string name, string text, Dictionary<string, string> captures)
    {
        if (name == "_") return captures;
        if (captures.TryGetValue(name, out var existing))
        {
            return existing == text ? captures : null!;
        }
        return new Dictionary<string, string>(captures, StringComparer.Ordinal) { [name] = text };
    }

    /// <summary>Verbatim source text of the contiguous run [start..end), including separators.</summary>
    private static string SliceRun(string sourceText, ChildSyntaxList list, int start, int endExclusive)
    {
        var firstSpan = list[start].Span;
        var lastSpan = list[endExclusive - 1].Span;
        return sourceText.Substring(firstSpan.Start, lastSpan.End - firstSpan.Start);
    }

    // ---- Span / position helpers ----

    private static IReadOnlyList<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        return starts;
    }

    private static void GetLocation(IReadOnlyList<int> lineStarts, TextSpan span, out int line, out int col, out int endLine, out int endCol)
    {
        var startLine = FindLine(lineStarts, span.Start);
        var endL = FindLine(lineStarts, span.End);
        line = startLine + 1;
        col = span.Start - lineStarts[startLine] + 1;
        endLine = endL + 1;
        endCol = span.End - lineStarts[endL] + 1;
    }

    private static int FindLine(IReadOnlyList<int> lineStarts, int offset)
    {
        var lo = 0;
        var hi = lineStarts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
