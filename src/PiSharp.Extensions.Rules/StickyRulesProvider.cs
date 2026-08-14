namespace PiSharp.Extensions.Rules;

/// <summary>
/// Built-in sticky-rule provider (P10 plan §5.5): synthesizes the always-apply
/// <c>RULES.md</c> rules — user <c>~/.pi/agent/RULES.md</c> → <c>RULES</c>, and the nearest
/// non-empty ancestor (from the session cwd) <c>RULES.md</c> → <c>RULES@project</c>. Both are
/// <see cref="RuleApplyMode.Always"/> so they are re-injected every turn
/// (<see cref="RulesEngine.PrepareMessagesAsync"/>) and thereby survive compaction by
/// construction. Name <c>rules-sticky</c>, priority 1000 (above foreign + file providers).
/// </summary>
public sealed class StickyRulesProvider : IRuleProvider
{
    public const string ProviderName = "rules-sticky";
    public const int ProviderPriority = 1000;

    public const string UserRuleName = "RULES";
    public const string ProjectRuleName = "RULES@project";

    private readonly string? _userStickyRulesPath;
    private readonly string? _cwd;
    private readonly bool _disableSticky;

    public StickyRulesProvider(string? userStickyRulesPath, string? cwd, bool disableSticky = false)
    {
        _userStickyRulesPath = userStickyRulesPath;
        _cwd = cwd;
        _disableSticky = disableSticky;
    }

    public string Name => ProviderName;

    public int Priority => ProviderPriority;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (_disableSticky)
            return Task.FromResult<IReadOnlyList<Rule>>([]);

        var rules = new List<Rule>();

        var userContent = ReadNonEmpty(_userStickyRulesPath);
        if (userContent is not null)
        {
            rules.Add(new Rule(
                Name: UserRuleName,
                Content: userContent,
                Path: _userStickyRulesPath,
                Priority: ProviderPriority,
                ApplyMode: RuleApplyMode.Always));
        }

        if (_cwd is not null)
        {
            var project = FindNearestNonEmptyProjectRules(_cwd);
            if (project is not null)
            {
                rules.Add(new Rule(
                    Name: ProjectRuleName,
                    Content: File.ReadAllText(project),
                    Path: project,
                    Priority: ProviderPriority,
                    ApplyMode: RuleApplyMode.Always));
            }
        }

        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    /// <summary>Walks <paramref name="cwd"/> and each ancestor, returning the first non-empty
    /// <c>RULES.md</c> path (shallowest-anchor to nearest), or null when none exists.</summary>
    public static string? FindNearestNonEmptyProjectRules(string cwd)
    {
        var current = Path.GetFullPath(cwd);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, "RULES.md");
            var content = ReadNonEmpty(candidate);
            if (content is not null) return candidate;

            var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return null;
    }

    private static string? ReadNonEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
