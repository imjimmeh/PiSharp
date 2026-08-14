using System.ComponentModel;
using System.Text;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Files;

public sealed class ReadTool(IExecutionEnv env, ReadToolOptions? options = null, PiSharp.Extensions.InternalUrlRegistry? urlRegistry = null, PiSharp.Extensions.FileContentExtractorRegistry? contentExtractors = null) : JsonTool<ReadToolInput, ReadToolDetails?>(ToolSchemas.FromType<ReadToolInput>())
{
    private static readonly System.Text.RegularExpressions.Regex InternalUrlSchemePattern = new("^([A-Za-z][A-Za-z0-9+.-]*)://", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private readonly IExecutionEnv _env = env;
    private readonly ReadToolOptions _options = options ?? new ReadToolOptions();
    private readonly PiSharp.Extensions.InternalUrlRegistry? _urlRegistry = urlRegistry;
    private readonly PiSharp.Extensions.FileContentExtractorRegistry? _contentExtractors = contentExtractors;

    public override string Name => "read";
    public override string Label => "read";
    public override string Description => $"Read the contents of a file. Supports text files and images (jpg, png, gif, webp). Images are sent as attachments. For text files, output is truncated to {Truncation.DefaultMaxLines} lines or {Truncation.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.";
    public override string PromptSnippet => "Read file contents";
    public override IReadOnlyList<string> PromptGuidelines => ["Use read to examine files instead of cat or sed."];

    public override async Task<AgentToolResult<ReadToolDetails?>> ExecuteAsync(string toolCallId, ReadToolInput parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<ReadToolDetails?>? onUpdate = null)
    {
        var path = parameters.Path;
        if (TryGetInternalUrlScheme(path, out var scheme))
            return await ReadInternalUrlAsync(scheme, path, parameters, cancellationToken).ConfigureAwait(false);

        var absolutePath = await PathUtilities.ResolvePathAsync(_env, path).ConfigureAwait(false);
        var bytesResult = await _env.ReadBinaryFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        var bytes = bytesResult.GetOrThrow(error => error);
        var mimeType = ImageUtilities.DetectSupportedImageMimeType(path, bytes);
        if (mimeType is not null)
        {
            var image = _options.AutoResizeImages ? await ImageUtilities.ResizeIfNeededAsync(bytes, mimeType, cancellationToken: cancellationToken).ConfigureAwait(false) : new ProcessedImage(mimeType, bytes);
            var note = $"Read image file [{image.MimeType}]" + (image.DimensionNote is null ? string.Empty : $"\n{image.DimensionNote}");
            return new AgentToolResult<ReadToolDetails?>([new TextContent(note), new ImageContent(image.MimeType, Convert.ToBase64String(image.Data))], null);
        }

        if (_contentExtractors is not null)
        {
            var extracted = await TryExtractContentAsync(_contentExtractors, path, bytes, cancellationToken).ConfigureAwait(false);
            if (extracted is not null)
            {
                var textResult = BuildTextResult(extracted.Text, parameters, path);
                return extracted.Note is null ? textResult : PrependText(textResult, extracted.Note.TrimEnd() + "\n");
            }
        }

        return BuildTextResult(Encoding.UTF8.GetString(bytes), parameters, path);
    }

    private static async Task<FileContentExtractionResult?> TryExtractContentAsync(PiSharp.Extensions.FileContentExtractorRegistry registry, string path, byte[] bytes, CancellationToken cancellationToken)
    {
        foreach (var extractor in registry.Extractors)
        {
            if (!extractor.CanHandle(path, bytes)) continue;
            FileContentExtractionResult? result;
            try
            {
                result = await extractor.ExtractAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // An extractor failure degrades to the UTF-8 fallback rather than aborting the read.
                return null;
            }
            if (result is not null) return result;
        }
        return null;
    }

    private static AgentToolResult<ReadToolDetails?> PrependText(AgentToolResult<ReadToolDetails?> result, string prefix)
    {
        var contents = result.Content.ToArray();
        for (var i = 0; i < contents.Length; i++)
        {
            if (contents[i] is TextContent text)
            {
                contents[i] = new TextContent(prefix + text.Text);
                return new AgentToolResult<ReadToolDetails?>(contents, result.Details, result.Terminate);
            }
        }
        return result;
    }

    private static bool TryGetInternalUrlScheme(string path, out string scheme)
    {
        var match = InternalUrlSchemePattern.Match(path);
        if (!match.Success)
        {
            scheme = string.Empty;
            return false;
        }
        scheme = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    private async Task<AgentToolResult<ReadToolDetails?>> ReadInternalUrlAsync(string scheme, string path, ReadToolInput parameters, CancellationToken cancellationToken)
    {
        var rest = path[(scheme.Length + 3)..];
        var queryIndex = rest.IndexOf('?');
        var target = queryIndex >= 0 ? rest[..queryIndex] : rest;
        var query = queryIndex >= 0 ? rest[(queryIndex + 1)..] : null;

        if (!PiSharp.Extensions.InternalUrlSecurity.TryParseTarget(target, out _))
            return new AgentToolResult<ReadToolDetails?>([new TextContent($"Blocked internal URL '{path}': target must be a relative plain path (no traversal, separators, or escapes).")], null);

        var registry = _urlRegistry;
        IInternalUrlResolver? resolver = null;
        if (registry is null || !registry.TryGet(scheme, out resolver))
        {
            var listing = registry is null ? "none registered" : string.Join(", ", registry.Schemes);
            return new AgentToolResult<ReadToolDetails?>([new TextContent($"Unknown internal URL scheme '{scheme}'. Registered schemes: {(listing.Length == 0 ? "none" : listing)}.")], null);
        }

        InternalUrlResult result;
        try
        {
            result = await resolver.ResolveAsync(new InternalUrlRequest(scheme, target, query, parameters.Offset, parameters.Limit), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AgentToolResult<ReadToolDetails?>([new TextContent($"Failed to resolve internal URL '{path}': {exception.Message}")], null);
        }

        if (!result.Resolved)
        {
            var detail = result.Error is null ? "resolution failed" : $"{result.Error.Kind}: {result.Error.Reason}";
            return new AgentToolResult<ReadToolDetails?>([new TextContent($"Failed to resolve internal URL '{path}' ({detail}).")], null);
        }

        return BuildTextResult(result.Content ?? string.Empty, parameters, null);
    }

    private static AgentToolResult<ReadToolDetails?> BuildTextResult(string text, ReadToolInput parameters, string? bashHintPath)
    {
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
            outputText = bashHintPath is null
                ? $"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit]"
                : $"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {bashHintPath} | head -c {Truncation.DefaultMaxBytes}]";
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
