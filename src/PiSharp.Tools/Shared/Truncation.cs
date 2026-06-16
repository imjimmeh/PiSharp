using System.Text;

namespace PiSharp.Tools.Shared;

public static class Truncation
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultMaxBytes = 50 * 1024;
    public const int GrepMaxLineLength = 500;

    public static string FormatSize(int bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0}KB";
        return $"{bytes / (1024.0 * 1024.0):0.0}MB";
    }

    public static TruncationResult TruncateHead(string content, TruncationOptions? options = null)
    {
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;
        var totalBytes = ByteCount(content);
        var lines = content.Split('\n');
        var totalLines = lines.Length;
        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult(content, false, null, totalLines, totalBytes, totalLines, totalBytes, false, false, maxLines, maxBytes);
        }

        if (lines.Length > 0 && ByteCount(lines[0]) > maxBytes)
        {
            return new TruncationResult(string.Empty, true, "bytes", totalLines, totalBytes, 0, 0, false, true, maxLines, maxBytes);
        }

        var output = new List<string>();
        var outputBytes = 0;
        var truncatedBy = "lines";
        for (var i = 0; i < lines.Length && i < maxLines; i++)
        {
            var lineBytes = ByteCount(lines[i]) + (i > 0 ? 1 : 0);
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                break;
            }

            output.Add(lines[i]);
            outputBytes += lineBytes;
        }

        var outputContent = string.Join("\n", output);
        return new TruncationResult(outputContent, true, truncatedBy, totalLines, totalBytes, output.Count, ByteCount(outputContent), false, false, maxLines, maxBytes);
    }

    public static TruncationResult TruncateTail(string content, TruncationOptions? options = null)
    {
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;
        var totalBytes = ByteCount(content);
        var lines = content.Split('\n');
        var totalLines = lines.Length;
        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult(content, false, null, totalLines, totalBytes, totalLines, totalBytes, false, false, maxLines, maxBytes);
        }

        var output = new List<string>();
        var outputBytes = 0;
        var truncatedBy = "lines";
        var lastLinePartial = false;
        for (var i = lines.Length - 1; i >= 0 && output.Count < maxLines; i--)
        {
            var lineBytes = ByteCount(lines[i]) + (output.Count > 0 ? 1 : 0);
            if (outputBytes + lineBytes > maxBytes)
            {
                truncatedBy = "bytes";
                if (output.Count == 0)
                {
                    var truncatedLine = TruncateStringToBytesFromEnd(lines[i], maxBytes);
                    output.Insert(0, truncatedLine);
                    outputBytes = ByteCount(truncatedLine);
                    lastLinePartial = true;
                }
                break;
            }

            output.Insert(0, lines[i]);
            outputBytes += lineBytes;
        }

        var outputContent = string.Join("\n", output);
        return new TruncationResult(outputContent, true, truncatedBy, totalLines, totalBytes, output.Count, ByteCount(outputContent), lastLinePartial, false, maxLines, maxBytes);
    }

    public static TruncatedLine TruncateLine(string line, int maxChars = GrepMaxLineLength)
        => line.Length <= maxChars ? new TruncatedLine(line, false) : new TruncatedLine($"{line[..maxChars]}... [truncated]", true);

    internal static int ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    private static string TruncateStringToBytesFromEnd(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) return text;
        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
        return Encoding.UTF8.GetString(bytes[start..]);
    }
}

public sealed record TruncationOptions(int? MaxLines = null, int? MaxBytes = null);

public sealed record TruncationResult(
    string Content,
    bool Truncated,
    string? TruncatedBy,
    int TotalLines,
    int TotalBytes,
    int OutputLines,
    int OutputBytes,
    bool LastLinePartial,
    bool FirstLineExceedsLimit,
    int MaxLines,
    int MaxBytes);

public sealed record TruncatedLine(string Text, bool WasTruncated);
