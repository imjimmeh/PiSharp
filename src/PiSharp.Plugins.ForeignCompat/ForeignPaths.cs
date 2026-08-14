using PiSharp.Compatibility.Settings;

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// Foreign root discovery, mirroring <c>PiResourceLoader</c>'s path semantics
/// (P11 plan §4.6): the global agent dir, the home dir, and every cwd ancestor
/// root-first. Each provider derives its per-tool dirs (<c>.claude/skills</c>,
/// <c>.clinerules</c>, …) from these roots, so home/global foreign sources come
/// first and per-repo sources after, matching the native context/skill walk.
/// </summary>
public static class ForeignPaths
{
    /// <summary>
    /// Returns the global agent dir, home dir, and cwd ancestors (root-first),
    /// deduplicated by canonical path.
    /// </summary>
    public static IReadOnlyList<string> DiscoverRoots(string cwd, string? homeDirectory = null)
    {
        var paths = PiAgentPaths.FromCwd(cwd, homeDirectory);
        var roots = new List<string> { paths.GlobalAgentDirectory, paths.HomeDirectory };
        roots.AddRange(AncestorsRootFirst(cwd));

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var full = Path.GetFullPath(root);
            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }

    /// <summary>
    /// Existing <c>&lt;root&gt;/&lt;toolDir&gt;/skills</c> directories for the given roots,
    /// canonicalized and deduplicated. Tool dirs are e.g. <c>.claude</c>, <c>.codex</c>,
    /// <c>.github</c>, <c>.cursor</c>, <c>.cline</c>, <c>.gemini</c>, <c>.opencode</c>.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSkillDirs(IReadOnlyList<string> roots, string toolDir)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var dir = Path.GetFullPath(Path.Combine(root, toolDir, "skills"));
            if (Directory.Exists(dir) && seen.Add(dir)) result.Add(dir);
        }
        return result;
    }

    /// <summary>
    /// Existing single-file or directory foreign rule candidates under each root, in
    /// root order. <paramref name="relativePaths"/> are repo-relative paths such as
    /// <c>.clinerules</c>, <c>.cursor/rules</c>, <c>.github/copilot-instructions.md</c>.
    /// </summary>
    public static IReadOnlyList<string> DiscoverRuleCandidates(IReadOnlyList<string> roots, IReadOnlyList<string> relativePaths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var relative in relativePaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if ((File.Exists(candidate) || Directory.Exists(candidate)) && seen.Add(candidate)) result.Add(candidate);
            }
        }
        return result;
    }

    /// <summary>
    /// cwd and each ancestor, shallowest first — byte-for-byte the same walk
    /// <c>PiResourceLoader</c> uses for native context/skill discovery.
    /// </summary>
    public static IEnumerable<string> AncestorsRootFirst(string cwd)
    {
        var directories = new List<string>();
        var current = Path.GetFullPath(cwd);
        while (!string.IsNullOrWhiteSpace(current))
        {
            directories.Add(current);
            var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        directories.Reverse();
        return directories;
    }
    /// <summary>Top-level markdown files (<c>.md</c> and <c>.mdc</c>) in a directory, IO-safe.</summary>
    public static IEnumerable<string> EnumerateMarkdownFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mdc", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    /// <summary>
    /// Recursive, IO-safe walk returning files whose name matches <paramref name="fileName"/>
    /// (case-insensitive), skipping any directory whose name is in <paramref name="skipDirNames"/>
    /// (e.g. <c>.git</c>, <c>node_modules</c>).
    /// </summary>
    public static IEnumerable<string> EnumerateFilesRecursive(string root, string fileName, IReadOnlySet<string>? skipDirNames = null)
    {
        if (!Directory.Exists(root)) yield break;
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(current);
                subdirectories = Directory.EnumerateDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            foreach (var subdirectory in subdirectories)
            {
                if (skipDirNames is not null && skipDirNames.Contains(Path.GetFileName(subdirectory)))
                    continue;
                pending.Push(subdirectory);
            }
        }
    }
}
