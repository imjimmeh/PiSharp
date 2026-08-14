namespace PiSharp.Extensions;

/// <summary>
/// Central traversal guards for internal URL targets (omp <c>skill://</c>
/// rules). Applied before dispatch on the raw parsed target and again on any
/// filesystem path a resolver returns (containment check).
/// </summary>
public static class InternalUrlSecurity
{
    /// <summary>
    /// Validates and normalizes a raw <c>scheme://</c> target into path
    /// segments. Returns false (blocked) for absolute forms (<c>/</c>,
    /// <c>\</c>, drive letters, <c>~</c>, <c>//</c>), <c>..</c> segments,
    /// escapes, percent-encoded traversal (<c>%2f</c>, <c>%5c</c>, <c>%2e</c>,
    /// <c>%252e</c>…), null bytes, and empty segments. <c>.</c> segments are
    /// removed.
    /// </summary>
    public static bool TryParseTarget(string target, out IReadOnlyList<string> segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(target)) return false;
        if (target.Contains('\0')) return false;
        if (target.StartsWith('/') || target.StartsWith('\\')) return false;
        if (target.StartsWith("//", StringComparison.Ordinal)) return false;
        if (target.StartsWith('~')) return false;
        if (target.Length >= 2 && char.IsLetter(target[0]) && target[1] == ':') return false;
        if (target.Contains('\\')) return false;
        if (ContainsTraversalEncoding(target)) return false;

        var parsed = new List<string>();
        foreach (var part in target.Split('/'))
        {
            if (part.Length == 0) return false;
            if (part == ".") continue;
            if (part == "..") return false;
            parsed.Add(part);
        }

        if (parsed.Count == 0) return false;
        segments = parsed;
        return true;
    }

    /// <summary>
    /// Post-resolution containment: verifies a returned filesystem path stays
    /// under <paramref name="allowedRoot"/> (case-insensitive on all platforms
    /// for consistency).
    /// </summary>
    public static bool IsContainedWithin(string absolutePath, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(allowedRoot)) return false;
        var fullPath = Path.GetFullPath(absolutePath);
        var fullRoot = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="segment"/> is a plain name: ASCII letters,
    /// digits, <c>.</c>, <c>_</c>, <c>-</c>, with no path separators, colons,
    /// or <c>..</c>.
    /// </summary>
    public static bool IsPlainName(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > 128) return false;
        if (segment.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in segment)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        }
        return true;
    }

    private static bool ContainsTraversalEncoding(string target)
    {
        for (var i = 0; i + 2 < target.Length; i++)
        {
            if (target[i] != '%') continue;
            var high = HexValue(target[i + 1]);
            var low = HexValue(target[i + 2]);
            if (high < 0 || low < 0) continue;
            var value = (high << 4) | low;
            if (value is '.' or '/' or '\\' or '%') return true;
        }
        return false;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
