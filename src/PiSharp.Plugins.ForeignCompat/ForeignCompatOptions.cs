using System.Text.RegularExpressions;

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// Wraps the P11 extension settings (P11 plan §9): per-source <c>enable*</c> toggles
/// (all default <c>true</c> — opt-out per source) and <c>include*</c>/<c>ignored*</c> globs.
/// Constructed via <see cref="FromApi"/> from <see cref="PiSharp.Extensions.IExtensionSettingsApi"/>
/// (the scoped wrapper applies the <c>extensions.pisharp-foreign-compat.*</c> namespace);
/// <see cref="Reload"/> re-reads the settings so a mid-session change is honored on the
/// next discovery (plan §4.3). Tests construct and configure directly.
/// </summary>
public sealed class ForeignCompatOptions
{
    private static readonly ConcurrentGlobMatcher SkillGlobMatcher = new();
    private static readonly ConcurrentGlobMatcher RuleGlobMatcher = new();

    private PiSharp.Extensions.IExtensionSettingsApi? _settings;

    public bool EnableClaudeUser { get; set; } = true;
    public bool EnableCodexUser { get; set; } = true;
    public bool EnableOpenCode { get; set; } = true;
    public bool EnableGithubUser { get; set; } = true;
    public bool EnableCursorUser { get; set; } = true;
    public bool EnableClineUser { get; set; } = true;
    public bool EnableGeminiUser { get; set; } = true;
    public bool EnableCopilotUser { get; set; } = true;
    public bool EnableRepoRules { get; set; } = true;

    public IReadOnlyList<string> IncludeSkills { get; set; } = [];
    public IReadOnlyList<string> IgnoredSkills { get; set; } = [];
    public IReadOnlyList<string> IncludeRules { get; set; } = [];
    public IReadOnlyList<string> IgnoredRules { get; set; } = [];

    /// <summary>Discovery roots (global agent dir, home, cwd ancestors) in precedence order.</summary>
    public IReadOnlyList<string> Roots { get; set; } = [];

    /// <summary>Repo root for repo-rule discovery — the session cwd.</summary>
    public string RepoRoot { get; set; } = string.Empty;

    /// <summary>
    /// Reads the effective settings through the scoped extension API. An unset toggle is
    /// treated as enabled (P11 plan §9 defaults), because the landed
    /// <c>Get&lt;T&gt;</c> returns <c>default</c> for absent keys.
    /// </summary>
    public static ForeignCompatOptions FromApi(PiSharp.Extensions.IExtensionSettingsApi settings, string cwd, string? homeDirectory = null)
    {
        var options = new ForeignCompatOptions
        {
            _settings = settings,
            Roots = ForeignPaths.DiscoverRoots(cwd, homeDirectory),
            RepoRoot = Path.GetFullPath(cwd),
        };
        options.Reload();
        return options;
    }

    /// <summary>Re-reads all settings; no-op when constructed directly (tests).</summary>
    public void Reload()
    {
        if (_settings is null) return;
        EnableClaudeUser = Enabled("enableClaudeUser");
        EnableCodexUser = Enabled("enableCodexUser");
        EnableOpenCode = Enabled("enableOpenCode");
        EnableGithubUser = Enabled("enableGithubUser");
        EnableCursorUser = Enabled("enableCursorUser");
        EnableClineUser = Enabled("enableClineUser");
        EnableGeminiUser = Enabled("enableGeminiUser");
        EnableCopilotUser = Enabled("enableCopilotUser");
        EnableRepoRules = Enabled("enableRepoRules");
        IncludeSkills = Globs("includeSkills");
        IgnoredSkills = Globs("ignoredSkills");
        IncludeRules = Globs("includeRules");
        IgnoredRules = Globs("ignoredRules");
    }

    private bool Enabled(string key) => _settings!.Get<bool?>(key) ?? true;

    private IReadOnlyList<string> Globs(string key) => _settings!.Get<string[]>(key) ?? [];

    /// <summary>Glob filter for skills: name or path must pass include and ignore globs.</summary>
    public bool MatchesSkill(string name, string? path)
        => Matches(name, path, IncludeSkills, IgnoredSkills, SkillGlobMatcher);

    /// <summary>Glob filter for rules: name or path must pass include and ignore globs.</summary>
    public bool MatchesRule(string name, string? path)
        => Matches(name, path, IncludeRules, IgnoredRules, RuleGlobMatcher);

    private static bool Matches(string name, string? path, IReadOnlyList<string> include, IReadOnlyList<string> ignored, ConcurrentGlobMatcher matcher)
    {
        if (ignored.Count > 0 && (matcher.MatchesAny(name, ignored) || (path is not null && matcher.MatchesAny(path, ignored))))
            return false;
        if (include.Count > 0 && !(matcher.MatchesAny(name, include) || (path is not null && matcher.MatchesAny(path, include))))
            return false;
        return true;
    }

    /// <summary>
    /// Compiled glob matcher with a small cache; globs mirror <c>PiResourceLoader.GlobMatch</c>
    /// (<c>**</c> → <c>.*</c>, <c>*</c> → <c>[^/]*</c>, <c>?</c> → <c>[^/]</c>, case-insensitive).
    /// </summary>
    private sealed class ConcurrentGlobMatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _cache = new(StringComparer.Ordinal);

        public bool MatchesAny(string candidate, IReadOnlyList<string> patterns)
        {
            var normalized = candidate.Replace('\\', '/');
            foreach (var pattern in patterns)
            {
                var regex = _cache.GetOrAdd(pattern, static p =>
                    new Regex("^" + Regex.Escape(p).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                        TimeSpan.FromSeconds(2)));
                if (regex.IsMatch(normalized)) return true;
            }
            return false;
        }
    }
}
