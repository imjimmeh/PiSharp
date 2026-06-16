using System.ComponentModel;
using System.Text;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Files;

public sealed class ReadTool(IExecutionEnv env, ReadToolOptions? options = null) : JsonTool<ReadToolInput, ReadToolDetails?>(ToolSchemas.FromType<ReadToolInput>())
{
    private readonly IExecutionEnv _env = env;
    private readonly ReadToolOptions _options = options ?? new ReadToolOptions();

    public override string Name => "read";
    public override string Label => "read";
    public override string Description => $"Read the contents of a file. Supports text files and images (jpg, png, gif, webp). Images are sent as attachments. For text files, output is truncated to {Truncation.DefaultMaxLines} lines or {Truncation.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.";
    public override string PromptSnippet => "Read file contents";
    public override IReadOnlyList<string> PromptGuidelines => ["Use read to examine files instead of cat or sed."];

    public override async Task<AgentToolResult<ReadToolDetails?>> ExecuteAsync(string toolCallId, ReadToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<ReadToolDetails?>? onUpdate = null)
    {
        var absolutePath = await PathUtilities.ResolvePathAsync(_env, parameters.Path).ConfigureAwait(false);
        var bytesResult = await _env.ReadBinaryFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        var bytes = bytesResult.GetOrThrow(error => error);
        var mimeType = ImageUtilities.DetectSupportedImageMimeType(parameters.Path, bytes);
        if (mimeType is not null)
        {
            var image = _options.AutoResizeImages ? await ImageUtilities.ResizeIfNeededAsync(bytes, mimeType, cancellationToken: cancellationToken).ConfigureAwait(false) : new ProcessedImage(mimeType, bytes);
            var note = $"Read image file [{image.MimeType}]" + (image.DimensionNote is null ? string.Empty : $"\n{image.DimensionNote}");
            return new AgentToolResult<ReadToolDetails?>([new TextContent(note), new ImageContent(image.MimeType, Convert.ToBase64String(image.Data))], null);
        }

        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        var startLine = parameters.Offset is > 0 ? parameters.Offset.Value - 1 : 0;
        if (startLine >= lines.Length) throw new InvalidOperationException($"Offset {parameters.Offset} is beyond end of file ({lines.Length} lines total)");
        if (parameters.Limit is < 0) throw new InvalidOperationException("limit must be greater than or equal to 0.");
        var startLineDisplay = startLine + 1;
        string selected;
        int? userLimitedLines = null;
        if (parameters.Limit is not null)
        {
            var endLine = Math.Min(startLine + parameters.Limit.Value, lines.Length);
            selected = string.Join("\n", lines[startLine..endLine]);
            userLimitedLines = endLine - startLine;
        }
        else
        {
            selected = string.Join("\n", lines[startLine..]);
        }

        var truncation = Truncation.TruncateHead(selected);
        string outputText;
        ReadToolDetails? details = null;
        if (truncation.FirstLineExceedsLimit)
        {
            var firstLineSize = Truncation.FormatSize(Encoding.UTF8.GetByteCount(lines[startLine]));
            outputText = $"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {parameters.Path} | head -c {Truncation.DefaultMaxBytes}]";
            details = new ReadToolDetails(truncation);
        }
        else if (truncation.Truncated)
        {
            var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
            var nextOffset = endLineDisplay + 1;
            outputText = truncation.Content + (truncation.TruncatedBy == "lines"
                ? $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {lines.Length}. Use offset={nextOffset} to continue.]"
                : $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {lines.Length} ({Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]");
            details = new ReadToolDetails(truncation);
        }
        else if (userLimitedLines is not null && startLine + userLimitedLines.Value < lines.Length)
        {
            var remaining = lines.Length - (startLine + userLimitedLines.Value);
            var nextOffset = startLine + userLimitedLines.Value + 1;
            outputText = $"{truncation.Content}\n\n[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
        }
        else
        {
            outputText = truncation.Content;
        }

        return new AgentToolResult<ReadToolDetails?>([new TextContent(outputText)], details);
    }

}

public sealed record ReadToolInput(
    [property: Description("Path to the file to read (relative or absolute)")]
    string Path,

    [property: Description("Line number to start reading from (1-indexed)")]
    int? Offset = null,

    [property: Description("Maximum number of lines to read")]
    int? Limit = null);
public sealed record ReadToolDetails(TruncationResult? Truncation = null);
public sealed record ReadToolOptions(bool AutoResizeImages = true);
