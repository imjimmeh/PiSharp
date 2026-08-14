using System.ComponentModel;
using System.Text;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ast.Ast;
using PiSharp.Ast.Hash;
using PiSharp.Tools;
using PiSharp.Tools.Edit;
using PiSharp.Tools.Shared;

namespace PiSharp.Ast.Tools;

/// <summary>
/// <c>ast_edit</c>: structural, staged rewrites. <c>Apply:false</c> returns a reviewable proposal
/// (per-op diff, match previews, content hash) and performs zero writes. Applying requires the same
/// <c>Ops</c> plus the proposal's <c>ExpectedHash</c>; a file whose hash moved is rejected before
/// any write. Ops apply sequentially, each against the previous op's output.
/// </summary>
public sealed class AstEditTool(IExecutionEnv env, AstLanguageRegistry registry, Func<bool>? isEnabled = null)
    : JsonTool<AstEditInput, AstEditDetails?>(ToolSchemas.FromType<AstEditInput>())
{
    internal const int PreviewCap = 20;
    private readonly IExecutionEnv _env = env;
    private readonly AstLanguageRegistry _registry = registry;
    private readonly Func<bool>? _isEnabled = isEnabled;

    public override string Name => "ast_edit";
    public override string Label => "ast_edit";
    public override string Description =>
        "Structurally rewrite C# code. With Apply:false returns a reviewable proposal (per-op diff, match previews, " +
        "content hash) without writing. To apply, call again with the same Ops, Apply:true, and ExpectedHash from the " +
        "proposal; a stale file is rejected before any write.";
    public override string PromptSnippet => "Apply safe structural code rewrites";

    public override async Task<AgentToolResult<AstEditDetails?>> ExecuteAsync(
        string toolCallId,
        AstEditInput parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<AstEditDetails?>? onUpdate = null)
    {
        if (_isEnabled is not null && !_isEnabled())
        {
            return new AgentToolResult<AstEditDetails?>([new TextContent("pisharp-structural-editing.ast.enabled is false")], null);
        }
        if (parameters.Ops.Count == 0)
        {
            throw new InvalidOperationException("ast_edit input is invalid. ops must contain at least one pattern/rewrite pair.");
        }

        return await FileMutationQueue.RunAsync(_env, parameters.Path, async () =>
        {
            var absolutePath = await PathUtilities.ResolvePathAsync(_env, parameters.Path).ConfigureAwait(false);
            var read = await _env.ReadTextFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            var rawContent = read.GetOrThrow(error => error);
            var stripped = EditDiff.StripBom(rawContent);
            var lineEnding = EditDiff.DetectLineEnding(stripped.Text);
            var normalized = EditDiff.NormalizeToLf(stripped.Text);

            var liveHash = ContentHasher.Sha256Hex(normalized);
            if (parameters.Apply && parameters.ExpectedHash is not null
                && !string.Equals(parameters.ExpectedHash, liveHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Stale proposal: {parameters.Path} changed after the proposal was made (content hash differs from ExpectedHash). " +
                    "Re-run ast_edit with Apply:false to re-propose, or read the file again to re-anchor.");
            }

            // Simulate the ops sequentially against the evolving content.
            var current = normalized;
            var proposals = new List<AstEditProposal>();
            for (var i = 0; i < parameters.Ops.Count; i++)
            {
                var op = parameters.Ops[i];
                var provider = ResolveProvider(op.Language, absolutePath);
                var result = provider.ApplyRewrite(current, op.Pattern, op.Rewrite, absolutePath);
                var opDiff = EditDiff.GenerateDiffString(EditDiff.NormalizeToLf(current), EditDiff.NormalizeToLf(result.NewContent));
                var preview = result.Matches.Take(PreviewCap).ToList();
                proposals.Add(new AstEditProposal(i, result.Matches.Count, preview, opDiff.Diff, WasStale: false));
                current = result.NewContent;
            }

            if (!parameters.Apply)
            {
                var proposalText = RenderProposals(proposals);
                return new AgentToolResult<AstEditDetails?>(
                    [new TextContent(proposalText)],
                    new AstEditDetails(proposals, Applied: false, Diff: null, FirstChangedLine: null, ContentHash: liveHash));
            }

            var finalContent = stripped.Bom + EditDiff.RestoreLineEndings(current, lineEnding);
            var write = await _env.WriteFileAsync(absolutePath, finalContent, cancellationToken).ConfigureAwait(false);
            write.GetOrThrow(error => error);

            var diff = EditDiff.GenerateDiffString(normalized, EditDiff.NormalizeToLf(current));
            return new AgentToolResult<AstEditDetails?>(
                [new TextContent($"Rewrote {parameters.Ops.Count} op(s) in {parameters.Path}.")],
                new AstEditDetails(proposals, Applied: true, diff.Diff, diff.FirstChangedLine,
                    ContentHasher.Sha256Hex(EditDiff.NormalizeToLf(current))));
        }).ConfigureAwait(false);
    }

    private IAstLanguageProvider ResolveProvider(string? language, string path)
    {
        var provider = _registry.Resolve(language, path);
        if (provider is null)
        {
            throw new InvalidOperationException(_registry.UnsupportedLanguageMessage(language, path));
        }
        return provider;
    }

    private static string RenderProposals(IReadOnlyList<AstEditProposal> proposals)
    {
        var sb = new StringBuilder();
        foreach (var proposal in proposals)
        {
            sb.Append("op ").Append(proposal.OpIndex).Append(": ").Append(proposal.MatchCount).AppendLine(" match(es)");
            foreach (var match in proposal.PreviewMatches)
            {
                sb.Append("  ").Append(match.Path).Append(':').Append(match.Line).Append(':').Append(match.Column)
                  .Append('-').Append(match.EndLine).Append(':').Append(match.EndColumn).Append(": ").AppendLine(match.Text);
            }
            sb.AppendLine(proposal.PreviewDiff);
        }
        return sb.ToString();
    }
}

public sealed record AstEditOp(
    [property: Description("Structural pattern with metavariables. Must parse as a single node.")]
    string Pattern,
    [property: Description("Replacement template using captured metavariables ($A, $$$ARGS). Must parse as a single node.")]
    string Rewrite,
    [property: Description("Language override ('csharp'). Defaults to auto-detection.")]
    string? Language = null);

public sealed record AstEditInput(
    [property: Description("Path to the file to rewrite.")]
    string Path,
    [property: Description("One or more pattern/rewrite ops, applied sequentially in order.")]
    IReadOnlyList<AstEditOp> Ops,
    [property: Description("false = proposal only (no writes); true = apply. Re-run the same Ops with the ExpectedHash from the proposal.")]
    bool Apply = false,
    [property: Description("Content hash (SHA-256 hex, from a prior proposal's ContentHash) the file must still have at apply time.")]
    string? ExpectedHash = null);

public sealed record AstEditDetails(
    IReadOnlyList<AstEditProposal> Proposals,
    bool Applied,
    string? Diff = null,
    int? FirstChangedLine = null,
    string? ContentHash = null);

public sealed record AstEditProposal(
    int OpIndex,
    int MatchCount,
    IReadOnlyList<AstMatch> PreviewMatches,
    string PreviewDiff,
    bool WasStale);
