namespace PiSharp.Git;

/// <summary>
/// Deterministic auto-grouping + Conventional-Commits-style message drafting for the
/// user-facing <c>/commit</c> path. Groups cluster changes by (Category, top-level
/// directory), order by score descending, and add a rule-based edge when a test group's
/// file stems mirror a source group's stems (the test group depends on the source group).
/// </summary>
public sealed class CommitPlanner
{
    private static readonly string[] ConventionalTypeByCategory = ["feat", "test", "docs", "chore", "chore"];

    public sealed record PlannedGroup(
        string Message,
        IReadOnlyList<string> Files,
        IReadOnlyList<int> DependsOn,
        ChangeCategory Category,
        string Scope);

    public sealed record PlanResult(
        bool Success,
        string? Error,
        IReadOnlyList<PlannedGroup>? Groups,
        ChangeInventory? Inventory);

    /// <summary>Build a commit plan from an already-captured inventory (auto-groups).</summary>
    public PlanResult Plan(ChangeInventory inventory, bool conventionalPrefix = true)
    {
        if (inventory.Changes.Count == 0)
        {
            return new PlanResult(true, null, [], inventory);
        }

        // Cluster by (category, first path segment).
        var clusters = new Dictionary<(ChangeCategory Category, string Scope), List<ChangeItem>>();
        foreach (var item in inventory.Changes)
        {
            var scope = TopLevelDirectory(item.Path);
            clusters.TryGetValue((item.Category, scope), out var list);
            if (list is null)
            {
                list = [];
                clusters[(item.Category, scope)] = list;
            }

            list.Add(item);
        }

        var groups = clusters
            .OrderByDescending(kv => ChangeClassifier.Score(kv.Key.Category))
            .ThenBy(kv => kv.Key.Scope, StringComparer.Ordinal)
            .Select(kv =>
            {
                var (category, scope) = kv.Key;
                var files = kv.Value.OrderBy(item => item.Path, StringComparer.Ordinal).Select(item => item.Path).ToList();
                return new PlannedGroup(
                    DraftMessage(category, scope, files, conventionalPrefix),
                    files,
                    [],
                    category,
                    scope);
            })
            .ToList();

        // Rule-based edges: a test group depends on the source group whose file stems it mirrors.
        var sourceStems = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Category != ChangeCategory.Source)
            {
                continue;
            }

            foreach (var file in group.Files)
            {
                var stem = NormalizeStem(Path.GetFileNameWithoutExtension(file));
                if (stem.Length == 0)
                {
                    continue;
                }

                if (!sourceStems.TryGetValue(stem, out var list))
                {
                    list = [];
                    sourceStems[stem] = list;
                }

                if (!list.Contains(groupIndex))
                {
                    list.Add(groupIndex);
                }
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Category != ChangeCategory.Test)
            {
                continue;
            }

            var deps = new HashSet<int>();
            foreach (var file in groups[i].Files)
            {
                var stem = NormalizeStem(Path.GetFileNameWithoutExtension(file));
                if (sourceStems.TryGetValue(stem, out var sourceIndexes))
                {
                    foreach (var sourceIndex in sourceIndexes)
                    {
                        deps.Add(sourceIndex);
                    }
                }
            }

            if (deps.Count > 0)
            {
                groups[i] = groups[i] with { DependsOn = deps.OrderBy(d => d).ToList() };
            }
        }

        return new PlanResult(true, null, groups, inventory);
    }

    /// <summary>Draft a Conventional-Commits-style subject for one group.</summary>
    public string DraftMessage(ChangeCategory category, string scope, IReadOnlyList<string> files, bool conventionalPrefix = true)
    {
        var type = ConventionalTypeByCategory[(int)category];
        var stem = CommonStem(files);
        if (!conventionalPrefix)
        {
            return $"{stem} (+{files.Count} files)";
        }

        return scope.Length > 0
            ? $"{type}({scope}): {stem} (+{files.Count} files)"
            : $"{type}: {stem} (+{files.Count} files)";
    }

    public static string TopLevelDirectory(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var separator = normalized.IndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string NormalizeStem(string stem)
    {
        foreach (var marker in new[] { ".Tests", ".Test", "_Tests", "_Test", ".Spec", "_Spec" })
        {
            if (stem.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^marker.Length];
            }
        }

        return stem;
    }

    private static string CommonStem(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        if (files.Count == 1)
        {
            return Path.GetFileNameWithoutExtension(files[0]);
        }

        var normalized = files.Select(f => f.Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var first = normalized[0];
        var last = normalized[^1];
        var prefixLength = 0;
        while (prefixLength < first.Length && prefixLength < last.Length
               && first[prefixLength] == last[prefixLength])
        {
            prefixLength++;
        }

        var prefix = first[..prefixLength];
        var lastSlash = prefix.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            return prefix[(lastSlash + 1)..];
        }

        return prefix.Length > 0 ? prefix : Path.GetFileNameWithoutExtension(files[0]);
    }
}
