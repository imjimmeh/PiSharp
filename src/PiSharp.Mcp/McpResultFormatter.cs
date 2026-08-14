using System.Text.Json;
using ModelContextProtocol.Protocol;
using PiSharp.Tools.Shared;

namespace PiSharp.Mcp;

/// <summary>
/// Formats an SDK <see cref="CallToolResult"/> into bounded text for the model: text blocks are
/// joined with newlines, structured content is compact-JSON serialized, image/audio blocks become
/// placeholder lines, embedded resources become URI lines, and the result is bounded with
/// <see cref="Truncation"/> (the same helper and defaults as built-in tools).
/// </summary>
public static class McpResultFormatter
{
    public const string ErrorPrefix = "[MCP error from {0}] ";

    /// <summary>
    /// Formats <paramref name="result"/> into a single bounded text block. When
    /// <paramref name="result.IsError"/> is true the text is prefixed with the per-server error
    /// marker so the failure is visible to the model without throwing.
    /// </summary>
    public static string Format(CallToolResult result, string serverName, TruncationOptions? truncation = null)
    {
        var text = FormatUnbounded(result);
        var maxLines = truncation?.MaxLines ?? Truncation.DefaultMaxLines;
        var maxBytes = truncation?.MaxBytes ?? Truncation.DefaultMaxBytes;
        var bounded = Truncation.TruncateHead(text, new TruncationOptions(maxLines, maxBytes));
        var output = bounded.Content;
        if (bounded.Truncated)
        {
            output += bounded.TruncatedBy == "lines"
                ? $"\n\n[Showing lines 1-{bounded.OutputLines} of {bounded.TotalLines}. Output truncated.]"
                : $"\n\n[Showing lines 1-{bounded.OutputLines} of {bounded.TotalLines} ({Truncation.FormatSize(maxBytes)} limit). Output truncated.]";
        }
        if (result.IsError == true)
            output = string.Format(ErrorPrefix, serverName) + output;
        return output;
    }

    /// <summary>Formats a single content block; returns null for unrecognized block types.</summary>
    public static string? FormatBlock(ContentBlock block)
    {
        switch (block)
        {
            case TextContentBlock text:
                return text.Text ?? string.Empty;
            case ImageContentBlock image:
                return $"[MCP image content block: {image.MimeType ?? "application/octet-stream"}, {image.Data.Length} bytes, data omitted]";
            case AudioContentBlock audio:
                return $"[MCP audio content block: {audio.MimeType ?? "application/octet-stream"}, {audio.Data.Length} bytes, data omitted]";
            case EmbeddedResourceBlock embedded:
                return $"[MCP resource: {embedded.Resource?.Uri ?? "(unknown)"}]";
            case ResourceLinkBlock link:
                return $"[MCP resource: {link.Uri}]";
            case ToolResultContentBlock nested:
                return FormatBlocks(nested.Content);
            default:
                return $"[MCP content block: {block.Type}]";
        }
    }

    private static string FormatUnbounded(CallToolResult result)
    {
        var parts = new List<string>();
        if (result.Content is not null)
        {
            foreach (var block in result.Content)
            {
                var text = FormatBlock(block);
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
        }

        if (result.StructuredContent is { } structured && structured.ValueKind != JsonValueKind.Undefined)
        {
            parts.Add(structured.GetRawText());
        }

        if (parts.Count == 0) return "(no content)";
        return string.Join("\n", parts);
    }

    private static string FormatBlocks(IList<ContentBlock>? blocks)
    {
        if (blocks is null || blocks.Count == 0) return string.Empty;
        return string.Join("\n", blocks
            .Select(FormatBlock)
            .Where(text => !string.IsNullOrEmpty(text)));
    }
}
