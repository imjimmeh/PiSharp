using System.ComponentModel;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Edit;

public sealed class EditTool(IExecutionEnv env) : JsonTool<EditToolInput, EditToolDetails?>(ToolSchemas.FromType<EditToolInput>())
{
    private readonly IExecutionEnv _env = env;

    public override string Name => "edit";
    public override string Label => "edit";
    public override string Description => "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.";
    public override string PromptSnippet => "Make precise file edits with exact text replacement";
    public override IReadOnlyList<string> PromptGuidelines =>
    [
        "Use edit for precise changes (edits[].oldText must match exactly)",
        "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
        "Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
        "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions."
    ];

    public override async Task<AgentToolResult<EditToolDetails?>> ExecuteAsync(string toolCallId, EditToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<EditToolDetails?>? onUpdate = null)
    {
        if (parameters.Edits.Count == 0) throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
        return await FileMutationQueue.RunAsync(_env, parameters.Path, async () =>
        {
            var absolutePath = await PathUtilities.ResolvePathAsync(_env, parameters.Path).ConfigureAwait(false);
            var read = await _env.ReadTextFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
            var rawContent = read.GetOrThrow(error => error);
            var stripped = EditDiff.StripBom(rawContent);
            var lineEnding = EditDiff.DetectLineEnding(stripped.Text);
            var normalizedContent = EditDiff.NormalizeToLf(stripped.Text);
            var applied = EditDiff.ApplyEditsToNormalizedContent(normalizedContent, parameters.Edits, parameters.Path);
            var finalContent = stripped.Bom + EditDiff.RestoreLineEndings(applied.NewContent, lineEnding);
            var write = await _env.WriteFileAsync(absolutePath, finalContent, cancellationToken).ConfigureAwait(false);
            write.GetOrThrow(error => error);
            var diff = EditDiff.GenerateDiffString(applied.BaseContent, applied.NewContent);
            return new AgentToolResult<EditToolDetails?>([new TextContent($"Successfully replaced {parameters.Edits.Count} block(s) in {parameters.Path}.")], new EditToolDetails(diff.Diff, diff.FirstChangedLine));
        }).ConfigureAwait(false);
    }

}

public sealed record EditToolInput(
    [property: Description("Path to the file to edit (relative or absolute)")]
    string Path,

    [property: Description("One or more targeted replacements. Each edit is matched against the original file, not incrementally.")]
    IReadOnlyList<EditReplacement> Edits);
public sealed record EditToolDetails(string Diff, int FirstChangedLine);
