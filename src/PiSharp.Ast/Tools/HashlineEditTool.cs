using System.ComponentModel;
using System.Text.RegularExpressions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ast.Hash;
using PiSharp.Tools;
using PiSharp.Tools.Edit;
using PiSharp.Tools.Shared;

namespace PiSharp.Ast.Tools;

/// <summary>
/// The P30 <c>edit</c> override (ExtensionOverridePolicy.OverrideBuiltIn). Keeps the built-in
/// exact-text behavior for <c>oldText</c> addressing and adds content-hash addressing via
/// <c>anchorHash</c> (12+ hex chars of a line block's SHA-256, from the <c>hashlines</c> tool).
/// Anchors are resolved and stale-checked against the live file inside <see cref="FileMutationQueue"/>,
/// so a file that changed after the anchors were read is rejected before any write.
/// </summary>
public sealed class HashlineEditTool(IExecutionEnv env) : JsonTool<HashlineEditInput, EditToolDetails?>(ToolSchemas.FromType<HashlineEditInput>())
{
    private static readonly Regex HexAnchor = new("^[0-9a-fA-F]{12,}$", RegexOptions.Compiled);
    private readonly IExecutionEnv _env = env;

    public override string Name => "edit";
    public override string Label => "edit";
    public override string Description =>
        "Edit a single file using exact text replacement. Every edit uses exactly one addressing mode: " +
        "edits[].oldText (exact text, must match a unique, non-overlapping region of the original file) or " +
        "edits[].anchorHash (content-hash anchor from the hashlines tool; 12+ hex chars; the current line block " +
        "becomes the old text, stale anchors are rejected before writing). If two changes affect the same block or " +
        "nearby lines, merge them into one edit instead of emitting overlapping edits.";
    public override string PromptSnippet => "Make precise file edits with exact text replacement or hash anchors";
    public override IReadOnlyList<string> PromptGuidelines =>
    [
        "Use edit for precise changes (edits[].oldText must match exactly, or use edits[].anchorHash from hashlines)",
        "When the file was read with hashlines, prefer edits[].anchorHash over retyping oldText; anchors are stale-checked.",
        "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
        "Each edit is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
        "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions."
    ];

    public override async Task<AgentToolResult<EditToolDetails?>> ExecuteAsync(
        string toolCallId,
        HashlineEditInput parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<EditToolDetails?>? onUpdate = null)
    {
        if (parameters.Edits.Count == 0)
        {
            throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
        }

        return await FileMutationQueue.RunAsync(_env, parameters.Path, async () =>
        {
            var absolutePath = await PathUtilities.ResolvePathAsync(_env, parameters.Path).ConfigureAwait(false);
            var read = await _env.ReadTextFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            var rawContent = read.GetOrThrow(error => error);
            var stripped = EditDiff.StripBom(rawContent);
            var lineEnding = EditDiff.DetectLineEnding(stripped.Text);
            var normalizedContent = EditDiff.NormalizeToLf(stripped.Text);

            var replacements = ResolveReplacements(parameters, normalizedContent);
            var applied = EditDiff.ApplyEditsToNormalizedContent(normalizedContent, replacements, parameters.Path);
            var finalContent = stripped.Bom + EditDiff.RestoreLineEndings(applied.NewContent, lineEnding);
            var write = await _env.WriteFileAsync(absolutePath, finalContent, cancellationToken).ConfigureAwait(false);
            write.GetOrThrow(error => error);

            var diff = EditDiff.GenerateDiffString(applied.BaseContent, applied.NewContent);
            return new AgentToolResult<EditToolDetails?>(
                [new TextContent($"Successfully replaced {parameters.Edits.Count} block(s) in {parameters.Path}.")],
                new EditToolDetails(diff.Diff, diff.FirstChangedLine));
        }).ConfigureAwait(false);
    }

    private static IReadOnlyList<EditReplacement> ResolveReplacements(HashlineEditInput parameters, string normalizedContent)
    {
        var index = new HashLineIndex(normalizedContent);
        var replacements = new List<EditReplacement>(parameters.Edits.Count);
        for (var i = 0; i < parameters.Edits.Count; i++)
        {
            var edit = parameters.Edits[i];
            var hasOldText = edit.OldText is not null;
            var hasAnchor = edit.AnchorHash is not null;
            if (hasOldText && hasAnchor)
            {
                throw new InvalidOperationException(
                    $"edits[{i}] must use exactly one addressing mode: oldText or anchorHash, not both, in {parameters.Path}.");
            }
            if (!hasOldText && !hasAnchor)
            {
                throw new InvalidOperationException(
                    $"edits[{i}] must specify either oldText or anchorHash in {parameters.Path}.");
            }

            var oldText = hasAnchor ? ResolveAnchor(index, edit.AnchorHash!, edit.AnchorLineCount, i, parameters.Path) : edit.OldText!;
            var newText = ApplyPlacement(oldText, edit.NewText, edit.Placement, i, parameters.Path);
            replacements.Add(new EditReplacement(oldText, newText));
        }
        return replacements;
    }

    private static string ResolveAnchor(HashLineIndex index, string anchor, int? lineCount, int editIndex, string path)
    {
        if (!HexAnchor.IsMatch(anchor))
        {
            throw new InvalidOperationException(
                $"edits[{editIndex}].anchorHash must be 12+ hex characters (from the hashlines tool) in {path}.");
        }

        var result = index.Resolve(anchor, lineCount ?? 1);
        if (!result.Found)
        {
            if (result.AmbiguousLines is not null)
            {
                throw new InvalidOperationException(
                    $"anchor is ambiguous in {path}: matches lines {string.Join(", ", result.AmbiguousLines)}. Provide more context or re-run hashlines.");
            }
            throw new InvalidOperationException(
                $"stale anchor — the file changed after the anchors were read (anchor not found, line range changed?) in {path}. Re-run hashlines to re-anchor.");
        }

        return result.Resolution!.BlockText;
    }

    private static string ApplyPlacement(string oldText, string newText, string? placement, int editIndex, string path)
    {
        switch (placement)
        {
            case null:
            case "":
            case "replace":
                return newText;
            case "insert_before":
                return newText + "\n" + oldText;
            case "insert_after":
                return oldText + "\n" + newText;
            default:
                throw new InvalidOperationException(
                    $"edits[{editIndex}].placement must be one of: replace, insert_before, insert_after (got '{placement}') in {path}.");
        }
    }
}

public sealed record HashlineEditReplacement(
    [property: Description("Exact text for one targeted replacement. Must be unique and non-overlapping. Omit when using anchorHash.")]
    string? OldText = null,
    [property: Description("Replacement text for this targeted edit.")]
    string NewText = "",
    [property: Description("Content-hash anchor of the target line block (12+ hex chars of SHA-256, from the hashlines tool). Resolves the exact current line content; stale anchors are rejected before writing.")]
    string? AnchorHash = null,
    [property: Description("Number of lines the anchor covers (default: 1). The block hash is the LF-joined block text.")]
    int? AnchorLineCount = null,
    [property: Description("Placement relative to the anchored line(s): replace | insert_before | insert_after (default: replace).")]
    string? Placement = null);

public sealed record HashlineEditInput(
    [property: Description("Path to the file to edit (relative or absolute)")]
    string Path,
    [property: Description("One or more targeted replacements. Each edit is matched against the original file, not incrementally.")]
    IReadOnlyList<HashlineEditReplacement> Edits);
