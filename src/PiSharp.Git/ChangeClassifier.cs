using PiSharp.Tools.Search;

namespace PiSharp.Git;

/// <summary>
/// Classification engine: assigns a category + score to every changed path, applies the
/// lockfile/generated exclusion patterns, and honors the <c>Files</c>/<c>Exclude</c> scoping
/// plus staged/unstaged filtering.
///
/// Scoring table (highest first): Source = 4, Test = 3, Docs = 2, Config = 1, Other = 1.
/// Excluded paths never appear in the change list — they are reported separately.
/// </summary>
public sealed class ChangeClassifier(GitPluginOptions options)
{
    private static readonly string[] TestMarkers = [".tests", ".test", "_tests", "_test", ".spec", "_spec"];

    private static readonly string[] TestDirectoryNames = ["tests", "test", "__tests__", "spec", "specs"];

    private static readonly string[] SourceExtensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt", ".swift", ".rb", ".php", ".c", ".cpp", ".h", ".hpp", ".m", ".mm", ".fs", ".fsx", ".dart", ".sh", ".ps1", ".bat", ".cmd"];

    private static readonly string[] DocExtensions = [".md", ".markdown", ".rst", ".txt", ".adoc", ".tex", ".pdf"];

    private static readonly string[] ConfigExtensions = [".json", ".jsonc", ".toml", ".yaml", ".yml", ".xml", ".ini", ".cfg", ".conf", ".config", ".props", ".targets", ".editorconfig", ".gitignore", ".gitattributes", ".env", ".npmrc", ".nvmrc"];

    /// <summary>
    /// Classify raw porcelain items. Applies scoping and filters, then assigns
    /// category/score. Returns the surviving changes plus the excluded paths.
    /// </summary>
    public ClassificationResult Classify(
        IReadOnlyList<ChangeItem> rawItems,
        IReadOnlyList<string>? scopeFiles = null,
        IReadOnlyList<string>? extraExcludes = null,
        bool includeStaged = true,
        bool includeUnstaged = true)
    {
        var excluded = new List<string>();
        var changes = new List<ChangeItem>();
        var effectiveExcludes = new List<string>(options.CommitExcludedPathPatterns);
        if (extraExcludes is { Count: > 0 })
        {
            effectiveExcludes.AddRange(extraExcludes);
        }

        foreach (var item in rawItems)
        {
            var normalizedPath = Normalize(item.Path);

            // Scoping: only explicitly requested paths participate (exact or glob).
            if (scopeFiles is { Count: > 0 } && !scopeFiles.Any(pattern => Matches(pattern, normalizedPath)))
            {
                continue;
            }

            // Exclusion patterns: lockfiles/generated never enter the change set.
            if (effectiveExcludes.Any(pattern => Matches(pattern, normalizedPath)))
            {
                excluded.Add(item.Path);
                continue;
            }

            // Staged/unstaged filtering: an item participates only when every component
            // it carries is included; a file with both staged and unstaged parts is
            // dropped when either side is excluded (conservative, predictable).
            if (!includeStaged && item.IsStaged)
            {
                continue;
            }

            if (!includeUnstaged && item.IsUnstaged)
            {
                continue;
            }

            var category = ClassifyPath(normalizedPath);
            changes.Add(item with { Category = category, Score = Score(category) });
        }

        return new ClassificationResult(changes, excluded);
    }

    /// <summary>Pure path classification (rename destination paths classify by destination).</summary>
    public ChangeCategory ClassifyPath(string path)
    {
        var normalized = Normalize(path);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Test-precedence: a test marker anywhere in the path wins over source/doc/config.
        var fileName = segments.Length > 0 ? segments[^1] : normalized;
        var fileNameStem = Path.GetFileNameWithoutExtension(fileName);
        if (segments.Any(segment => TestDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
            || TestMarkers.Any(marker => fileNameStem.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return ChangeCategory.Test;
        }

        if (segments.Any(segment => segment.Equals("docs", StringComparison.OrdinalIgnoreCase)))
        {
            return ChangeCategory.Docs;
        }

        var extension = Path.GetExtension(fileName);
        if (SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return ChangeCategory.Source;
        }

        if (DocExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return ChangeCategory.Docs;
        }

        if (ConfigExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return ChangeCategory.Config;
        }

        return ChangeCategory.Other;
    }

    public static int Score(ChangeCategory category) => category switch
    {
        ChangeCategory.Source => 4,
        ChangeCategory.Test => 3,
        ChangeCategory.Docs => 2,
        ChangeCategory.Config => 1,
        ChangeCategory.Other => 1,
        _ => 1
    };

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool Matches(string pattern, string normalizedPath)
        => GlobMatcher.IsMatch(pattern, normalizedPath);
}

/// <summary>Classified changes plus policy-excluded paths.</summary>
public sealed record ClassificationResult(
    IReadOnlyList<ChangeItem> Changes,
    IReadOnlyList<string> ExcludedFiles);
