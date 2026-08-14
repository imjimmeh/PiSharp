using PiSharp.Tools.Edit;

namespace PiSharp.Ast.Hash;

/// <summary>
/// Result of resolving a hashline anchor against a file's LF-normalized content.
/// </summary>
public sealed record HashLineResolution(int StartLine, string BlockText, string FullHash);

public sealed record HashLineResolveResult(
    bool Found,
    HashLineResolution? Resolution = null,
    IReadOnlyList<int>? AmbiguousLines = null,
    string? Error = null);

/// <summary>
/// Pure line-block hash index. Hashes are computed over LF-normalized content
/// (see <see cref="EditDiff.NormalizeToLf"/>) so a file hashes identically across
/// CRLF/LF hosts. A block hash is the SHA-256 of <c>lineCount</c> lines joined
/// with <c>\n</c> and no trailing newline.
/// </summary>
public sealed class HashLineIndex
{
    private readonly string[] _lines;

    public HashLineIndex(string content)
    {
        var normalized = EditDiff.NormalizeToLf(content);
        var lines = normalized.Split('\n');
        // A trailing newline produces a phantom empty segment; drop it so
        // "a\nb\n" and "a\nb" both describe two editable lines.
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }
        _lines = lines;
    }

    public int LineCount => _lines.Length;

    /// <summary>Full SHA-256 hex of the block starting at <paramref name="startLine"/> (1-indexed).</summary>
    public string BlockHash(int startLine, int lineCount = 1)
    {
        ValidateRange(startLine, lineCount);
        var end = Math.Min(startLine + lineCount - 1, _lines.Length);
        var block = string.Join("\n", _lines[(startLine - 1)..end]);
        return ContentHasher.Sha256Hex(block);
    }

    /// <summary>Rendered <c>@&lt;12-hex&gt;</c> anchor for the block starting at <paramref name="startLine"/>.
    /// The anchor is the first 12 hex chars of the block's SHA-256 — a prefix of
    /// <see cref="BlockHash"/>, never a second hash of the hash.</summary>
    public string AnchorHash(int startLine, int lineCount = 1)
        => BlockHash(startLine, lineCount)[..ContentHasher.AnchorHexLength];

    /// <summary>The raw text of one line (1-indexed), without its line ending.</summary>
    public string LineText(int line) => _lines[line - 1];

    /// <summary>
    /// Resolves a hex anchor (12+ chars, prefix of a block's full SHA-256) to exactly one
    /// block start. Zero matches and ambiguous matches are reported rather than guessed.
    /// </summary>
    public HashLineResolveResult Resolve(string anchor, int lineCount = 1)
    {
        ValidateRange(1, lineCount);
        var matches = new List<int>();
        for (var line = 1; line <= _lines.Length; line++)
        {
            var hash = BlockHash(line, lineCount);
            if (hash.StartsWith(anchor, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(line);
            }
        }

        if (matches.Count == 0)
        {
            return new HashLineResolveResult(false, Error: "anchor not found (line range changed?)");
        }

        if (matches.Count > 1)
        {
            return new HashLineResolveResult(false, AmbiguousLines: matches, Error: "anchor is ambiguous");
        }

        var start = matches[0];
        var end = Math.Min(start + lineCount - 1, _lines.Length);
        var blockText = string.Join("\n", _lines[(start - 1)..end]);
        return new HashLineResolveResult(true, new HashLineResolution(start, blockText, BlockHash(start, lineCount)));
    }

    private void ValidateRange(int startLine, int lineCount)
    {
        if (lineCount <= 0) throw new ArgumentOutOfRangeException(nameof(lineCount), "lineCount must be >= 1.");
        if (startLine < 1 || startLine > _lines.Length)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"startLine {startLine} is out of range (file has {_lines.Length} lines).");
    }
}
