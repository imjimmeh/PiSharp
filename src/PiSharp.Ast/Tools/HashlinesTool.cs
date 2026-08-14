using PiSharp.Ast.Hash;
using System.ComponentModel;
using System.Text;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools;
using PiSharp.Tools.Edit;
using PiSharp.Tools.Shared;

namespace PiSharp.Ast.Tools;

public sealed class HashlinesTool(IExecutionEnv env) : JsonTool<HashlinesInput, HashlinesDetails?>(ToolSchemas.FromType<HashlinesInput>())
{
    internal const int DefaultLimit = 500;
    private readonly IExecutionEnv _env = env;

    public override string Name => "hashlines";
    public override string Label => "hashlines";
    public override string Description =>
        "Render a file with each line prefixed by its content-hash anchor (@<12-hex>). Use anchors as edits[].anchorHash " +
        "in the edit tool to address lines without retyping them. Hashes are stable per content (LF-normalized).";
    public override string PromptSnippet => "Show a file with content-hash line anchors";
    public override IReadOnlyList<string> PromptGuidelines =>
    [
        "When the file was read with hashlines, prefer edits[].anchorHash over retyping oldText; anchors are stale-checked."
    ];

    public override async Task<AgentToolResult<HashlinesDetails?>> ExecuteAsync(
        string toolCallId,
        HashlinesInput parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<HashlinesDetails?>? onUpdate = null)
    {
        var absolutePath = await PathUtilities.ResolvePathAsync(_env, parameters.Path).ConfigureAwait(false);
        var read = await _env.ReadTextFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        var content = read.GetOrThrow(error => error);
        var stripped = EditDiff.StripBom(content);

        var index = new HashLineIndex(stripped.Text);
        var startLine = parameters.Offset is > 0 ? parameters.Offset.Value : 1;
        if (startLine > index.LineCount)
        {
            throw new InvalidOperationException($"Offset {parameters.Offset} is beyond end of file ({index.LineCount} lines total)");
        }
        if (parameters.Limit is < 0)
        {
            throw new InvalidOperationException("limit must be greater than or equal to 0.");
        }

        var limit = parameters.Limit ?? DefaultLimit;
        var endLine = Math.Min(startLine + limit - 1, index.LineCount);
        var width = endLine.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var rendered = new StringBuilder();
        for (var line = startLine; line <= endLine; line++)
        {
            var anchor = index.AnchorHash(line);
            var number = line.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width);
            rendered.Append('@').Append(anchor).Append("  ").Append(number).Append("  ").Append(index.LineText(line));
            if (line < endLine) rendered.Append('\n');
        }

        var selected = rendered.ToString();
        var truncation = Truncation.TruncateHead(selected);
        string outputText;
        HashlinesDetails? details = null;
        if (truncation.Truncated)
        {
            var endLineDisplay = startLine + truncation.OutputLines - 1;
            var nextOffset = endLineDisplay + 1;
            outputText = truncation.Content +
                (truncation.TruncatedBy == "lines"
                    ? $"\n\n[Showing lines {startLine}-{endLineDisplay} of {index.LineCount}. Use offset={nextOffset} to continue.]"
                    : $"\n\n[Showing lines {startLine}-{endLineDisplay} of {index.LineCount} ({Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]");
            details = new HashlinesDetails(truncation, LinesTruncated: true);
        }
        else if (limit < index.LineCount - startLine + 1)
        {
            var remaining = index.LineCount - endLine;
            outputText = $"{selected}\n\n[{remaining} more lines in file. Use offset={endLine + 1} to continue.]";
        }
        else
        {
            outputText = selected;
        }

        return new AgentToolResult<HashlinesDetails?>([new TextContent(outputText)], details);
    }
}

public sealed record HashlinesInput(
    [property: Description("Path to the file to render with line anchors.")]
    string Path,
    [property: Description("First line to render (1-indexed, default 1).")]
    int? Offset = null,
    [property: Description("Maximum number of lines to render (default: 500).")]
    int? Limit = null);

public sealed record HashlinesDetails(TruncationResult? Truncation = null, bool LinesTruncated = false);
