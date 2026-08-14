using PiSharp.Extensions;

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// Shared helpers for foreign rule providers: IO-safe file reads and glob filtering.
/// </summary>
internal static class ForeignRuleFiles
{
    public static bool TryRead(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            content = string.Empty;
            return false;
        }
    }
}

/// <summary>
/// Cline rules (P11 plan §5.2): the whole-file <c>.clinerules</c> (nearest root wins —
/// repo shadows user), plus the <c>.clinerules/</c> and <c>.cli/rules/</c> markdown
/// collections from every root. Toggle: <c>enableClineUser</c>.
/// </summary>
public sealed class ClineRuleProvider : IRuleProvider
{
    private readonly ForeignCompatOptions _options;

    public ClineRuleProvider(ForeignCompatOptions options) => _options = options;

    public string Name => "cline";

    public int Priority => ForeignCompatTiers.Cline;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableClineUser) return Task.FromResult<IReadOnlyList<Rule>>([]);
        var rules = new List<Rule>();
        var candidates = ForeignPaths.DiscoverRuleCandidates(_options.Roots, [".clinerules", ".cli/rules"]);
        var wholeFile = candidates.LastOrDefault(path =>
            File.Exists(path) && string.Equals(Path.GetFileName(path).TrimStart('.'), "clinerules", StringComparison.OrdinalIgnoreCase));
        if (wholeFile is not null && ForeignRuleFiles.TryRead(wholeFile, out var content))
            AddRules(ForeignRuleParsers.ParseClineRules(content, wholeFile, Name, Priority), rules);

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            foreach (var file in ForeignPaths.EnumerateMarkdownFiles(candidate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ForeignRuleFiles.TryRead(file, out var fileContent)) continue;
                AddRules(ForeignRuleParsers.ParseClineRules(fileContent, file, Name, Priority), rules);
            }
        }
        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    private void AddRules(IReadOnlyList<Rule> parsed, List<Rule> rules)
    {
        foreach (var rule in parsed)
            if (_options.MatchesRule(rule.Name, rule.Path)) rules.Add(rule);
    }
}

/// <summary>
/// Cursor rules (P11 plan §5.2): <c>.cursorrules</c> (nearest root wins) and
/// <c>.cursor/rules/</c> — MDC (<c>*.mdc</c>, frontmatter-gated) and plain markdown.
/// Toggle: <c>enableCursorUser</c>.
/// </summary>
public sealed class CursorRuleProvider : IRuleProvider
{
    private readonly ForeignCompatOptions _options;

    public CursorRuleProvider(ForeignCompatOptions options) => _options = options;

    public string Name => "cursor";

    public int Priority => ForeignCompatTiers.Cursor;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableCursorUser) return Task.FromResult<IReadOnlyList<Rule>>([]);
        var rules = new List<Rule>();
        var candidates = ForeignPaths.DiscoverRuleCandidates(_options.Roots, [".cursorrules", ".cursor/rules"]);

        var cursorrules = candidates.LastOrDefault(path =>
            File.Exists(path) && string.Equals(Path.GetFileName(path), ".cursorrules", StringComparison.OrdinalIgnoreCase));
        if (cursorrules is not null && ForeignRuleFiles.TryRead(cursorrules, out var content))
            AddRules(ForeignRuleParsers.ParseCursorRules(content, cursorrules, Name, Priority), rules);

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            foreach (var file in ForeignPaths.EnumerateMarkdownFiles(candidate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ForeignRuleFiles.TryRead(file, out var fileContent)) continue;
                var parsed = Path.GetExtension(file).Equals(".mdc", StringComparison.OrdinalIgnoreCase)
                    ? ForeignRuleParsers.ParseMdc(fileContent, file, Name, Priority)
                    : ForeignRuleParsers.ParsePlainMarkdown(fileContent, file, Name, Priority, "cursor");
                AddRules(parsed, rules);
            }
        }
        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    private void AddRules(IReadOnlyList<Rule> parsed, List<Rule> rules)
    {
        foreach (var rule in parsed)
            if (_options.MatchesRule(rule.Name, rule.Path)) rules.Add(rule);
    }
}

/// <summary>
/// Copilot applyTo (<c>.github/copilot-instructions.md</c>), nearest root wins —
/// whole-repo rule named <c>copilot</c> (P10's <see cref="Rule"/> carries no file globs,
/// plan §13). Toggle: <c>enableCopilotUser</c>.
/// </summary>
public sealed class CopilotRuleProvider : IRuleProvider
{
    private readonly ForeignCompatOptions _options;

    public CopilotRuleProvider(ForeignCompatOptions options) => _options = options;

    public string Name => "copilot";

    public int Priority => ForeignCompatTiers.Copilot;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableCopilotUser) return Task.FromResult<IReadOnlyList<Rule>>([]);
        var rules = new List<Rule>();
        var candidates = ForeignPaths.DiscoverRuleCandidates(_options.Roots, [".github/copilot-instructions.md"]);
        var nearest = candidates.LastOrDefault(File.Exists);
        if (nearest is not null && ForeignRuleFiles.TryRead(nearest, out var content))
        {
            foreach (var rule in ForeignRuleParsers.ParseCopilotApplyTo(content, nearest, Name, Priority))
                if (_options.MatchesRule(rule.Name, rule.Path)) rules.Add(rule);
        }
        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }
}

/// <summary>
/// Gemini rules (P11 plan §5.2): <c>.gemini/rules/</c> markdown files from every root,
/// and the config-root files <c>GEMINIRULES.md</c>/<c>gemini-rules.md</c> (nearest wins).
/// Toggle: <c>enableGeminiUser</c>.
/// </summary>
public sealed class GeminiRuleProvider : IRuleProvider
{
    private readonly ForeignCompatOptions _options;

    public GeminiRuleProvider(ForeignCompatOptions options) => _options = options;

    public string Name => "gemini";

    public int Priority => ForeignCompatTiers.Gemini;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableGeminiUser) return Task.FromResult<IReadOnlyList<Rule>>([]);
        var rules = new List<Rule>();
        var candidates = ForeignPaths.DiscoverRuleCandidates(_options.Roots, [".gemini/rules", "GEMINIRULES.md", "gemini-rules.md"]);

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                foreach (var file in ForeignPaths.EnumerateMarkdownFiles(candidate))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ForeignRuleFiles.TryRead(file, out var content)) continue;
                    AddRules(ForeignRuleParsers.ParseGeminiRules(content, file, Name, Priority), rules);
                }
            }
        }

        var configRoot = candidates.LastOrDefault(path =>
            File.Exists(path)
            && (string.Equals(Path.GetFileName(path), "GEMINIRULES.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), "gemini-rules.md", StringComparison.OrdinalIgnoreCase)));
        if (configRoot is not null && ForeignRuleFiles.TryRead(configRoot, out var rootContent))
            AddRules(ForeignRuleParsers.ParseGeminiRules(rootContent, configRoot, Name, Priority), rules);

        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    private void AddRules(IReadOnlyList<Rule> parsed, List<Rule> rules)
    {
        foreach (var rule in parsed)
            if (_options.MatchesRule(rule.Name, rule.Path)) rules.Add(rule);
    }
}

/// <summary>
/// Repo rules (P11 plan §5.2): nested <c>**/RULES.md</c> and <c>.pisharp/RULES.md</c>
/// under the session repo, skipping the repo-root <c>RULES.md</c> that P10's sticky
/// engine owns (nearest-project surface, GAP-18) so no canonical file is double-injected.
/// Toggle: <c>enableRepoRules</c>.
/// </summary>
public sealed class RepoRuleProvider : IRuleProvider
{
    private static readonly IReadOnlySet<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".venv", "venv", "dist", "build",
    };

    private readonly ForeignCompatOptions _options;

    public RepoRuleProvider(ForeignCompatOptions options) => _options = options;

    public string Name => "repo";

    public int Priority => ForeignCompatTiers.Repo;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableRepoRules || string.IsNullOrWhiteSpace(_options.RepoRoot)) return Task.FromResult<IReadOnlyList<Rule>>([]);
        var rules = new List<Rule>();
        foreach (var file in ForeignPaths.EnumerateFilesRecursive(_options.RepoRoot, "RULES.md", SkipDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(_options.RepoRoot), StringComparison.OrdinalIgnoreCase))
                continue; // repo-root RULES.md is P10's nearest-project sticky surface
            if (!ForeignRuleFiles.TryRead(file, out var content)) continue;
            AddRules(ForeignRuleParsers.ParseRepoRules(content, file, Name, Priority, _options.RepoRoot), rules);
        }
        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    private void AddRules(IReadOnlyList<Rule> parsed, List<Rule> rules)
    {
        foreach (var rule in parsed)
            if (_options.MatchesRule(rule.Name, rule.Path)) rules.Add(rule);
    }
}

/// <summary>
/// GitHub rules (P11 plan §5.2): <c>.github/rules/</c> markdown/MDC files from every
/// root. (The <c>.github/copilot-instructions.md</c> rules half belongs to the Copilot
/// provider — it is not re-ingested here to avoid double-injection.) Toggle:
/// <c>enableGithubUser</c>.
/// </summary>
public sealed class GithubRuleProvider : IRuleProvider
{
    private readonly ForeignCompatOptions _options;

    public GithubRuleProvider(ForeignCompatOptions options) => _options = options;

    public string Name => "github";

    public int Priority => ForeignCompatTiers.Github;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableGithubUser) return Task.FromResult<IReadOnlyList<Rule>>([]);
        var rules = new List<Rule>();
        foreach (var candidate in ForeignPaths.DiscoverRuleCandidates(_options.Roots, [".github/rules"]))
        {
            if (!Directory.Exists(candidate)) continue;
            foreach (var file in ForeignPaths.EnumerateMarkdownFiles(candidate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ForeignRuleFiles.TryRead(file, out var content)) continue;
                var parsed = Path.GetExtension(file).Equals(".mdc", StringComparison.OrdinalIgnoreCase)
                    ? ForeignRuleParsers.ParseMdc(content, file, Name, Priority)
                    : ForeignRuleParsers.ParseGithubRules(content, file, Name, Priority);
                AddRules(parsed, rules);
            }
        }
        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    private void AddRules(IReadOnlyList<Rule> parsed, List<Rule> rules)
    {
        foreach (var rule in parsed)
            if (_options.MatchesRule(rule.Name, rule.Path)) rules.Add(rule);
    }
}
