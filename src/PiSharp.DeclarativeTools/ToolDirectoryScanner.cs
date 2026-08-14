namespace PiSharp.DeclarativeTools;

/// <summary>
/// Discovers candidate tool files inside tool directories (plan §5.1, §7).
/// Accepts the file form (<c>tools/foo.ext</c>) and the directory form
/// (<c>tools/&lt;name&gt;/index.ext</c>) exactly one level deep; files at deeper
/// nesting, files with other extensions, and names starting with <c>.</c> or
/// <c>_</c> are ignored. Results are ordered ordinally by path so duplicate
/// names resolve deterministically (first wins).
/// </summary>
public sealed class ToolDirectoryScanner
{
    private static readonly string[] AcceptedExtensions = [".md", ".json", ".sh", ".bash", ".py", ".ts"];

    /// <summary>
    /// Resolves the effective tool directories: the configured <paramref name="configured"/>
    /// entries (absolute, or relative to <paramref name="cwd"/>) when non-empty, otherwise the
    /// built-in defaults <c>~/.pi/PiSharp/tools</c> and <c>&lt;cwd&gt;/.pi/PiSharp/tools</c>
    /// (mirroring the global/project split of <c>PiAgentPaths</c>).
    /// </summary>
    public IReadOnlyList<string> ResolveToolDirectories(IReadOnlyList<string> configured, string cwd)
    {
        if (configured.Count > 0)
        {
            return configured
                .Select(dir => Path.IsPathRooted(dir) ? dir : Path.GetFullPath(dir, cwd))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var defaults = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) defaults.Add(Path.Combine(home, ".pi", "PiSharp", "tools"));
        defaults.Add(Path.Combine(cwd, ".pi", "PiSharp", "tools"));
        return defaults;
    }

    /// <summary>
    /// Enumerates candidate tool files in the given directories. Missing
    /// directories are skipped silently (not an error).
    /// </summary>
    public IReadOnlyList<ToolFile> Scan(IReadOnlyList<string> toolDirectories)
    {
        var files = new List<ToolFile>();
        foreach (var dir in toolDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            ScanFileForm(dir, files);
            ScanIndexForm(dir, files);
        }
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    private static void ScanFileForm(string dir, List<ToolFile> files)
    {
        foreach (var path in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (IsIgnoredName(fileName)) continue;
            if (KindFor(Path.GetExtension(fileName)) is not { } kind) continue;
            files.Add(new ToolFile(path, kind, ToolFileShape.File));
        }
    }

    private static void ScanIndexForm(string dir, List<ToolFile> files)
    {
        foreach (var subdir in Directory.EnumerateDirectories(dir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var dirName = Path.GetFileName(subdir);
            if (IsIgnoredName(dirName)) continue;
            foreach (var path in Directory.EnumerateFiles(subdir).OrderBy(p => p, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(path);
                if (!fileName.StartsWith("index.", StringComparison.OrdinalIgnoreCase)) continue;
                if (KindFor(Path.GetExtension(fileName)) is not { } kind) continue;
                files.Add(new ToolFile(path, kind, ToolFileShape.Index));
            }
        }
    }

    private static bool IsIgnoredName(string name)
        => name.StartsWith('.') || name.StartsWith('_');

    private static DeclarativeToolKind? KindFor(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".md" => DeclarativeToolKind.Markdown,
            ".json" => DeclarativeToolKind.Json,
            ".sh" or ".bash" or ".py" or ".ts" => DeclarativeToolKind.Script,
            _ => null
        };
}
