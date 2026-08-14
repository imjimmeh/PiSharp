using PiSharp.Extensions;
using YamlDotNet.Serialization;

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// Pure per-format parsers (P11 plan §5.3): foreign rule files → normalized
/// <see cref="Rule"/> records. All foreign formats are classic always-apply rules
/// (plan §5.2) — every emitted <see cref="Rule"/> uses
/// <see cref="RuleApplyMode.Always"/> and never sets <c>TriggerPattern</c>, so P10's
/// stream-trigger path stays P10's own. Names are stable: derived from the file path.
/// </summary>
public static class ForeignRuleParsers
{
    private static readonly YamlDotNet.Serialization.IDeserializer MdcDeserializer = new DeserializerBuilder().Build();

    /// <summary>
    /// <c>.clinerules</c> whole-file, or one file of a <c>.clinerules/</c> collection
    /// (or <c>.cli/rules/</c>). Name: <c>clinerules</c> for the single file,
    /// <c>clinerules:&lt;stem&gt;</c> for collection entries.
    /// </summary>
    public static IReadOnlyList<Rule> ParseClineRules(string content, string path, string source, int priority)
    {
        var isWholeFile = string.Equals(Path.GetFileName(path).TrimStart('.'), "clinerules", StringComparison.OrdinalIgnoreCase);
        var name = isWholeFile
            ? "clinerules"
            : $"clinerules:{NormalizeStem(Path.GetFileNameWithoutExtension(path))}";
        return [PlainRule(name, content, path, source, priority)];
    }

    /// <summary><c>.cursorrules</c> single file → rule named <c>cursorrules</c>.</summary>
    public static IReadOnlyList<Rule> ParseCursorRules(string content, string path, string source, int priority)
        => [PlainRule("cursorrules", content, path, source, priority)];

    /// <summary>
    /// Cursor MDC (<c>.cursor/rules/*.mdc</c>): YAML frontmatter
    /// <c>description</c>/<c>globs</c>/<c>alwaysApply</c>; body → rule. Rules with
    /// <c>alwaysApply: false</c> are skipped. Name: <c>mdc:&lt;stem&gt;</c>.
    /// <c>globs</c> is parsed but not representable on P10's <see cref="Rule"/> (no file-glob
    /// field) — the rule applies whole-repo, per plan §13.
    /// </summary>
    public static IReadOnlyList<Rule> ParseMdc(string content, string path, string source, int priority)
    {
        var (frontmatter, body) = ParseMdcFrontmatter(content);
        if (TryGetBool(frontmatter, "alwaysApply") == false) return [];
        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(path));
        var ruleContent = string.IsNullOrWhiteSpace(body) ? content.Trim() : body;
        return [PlainRule($"mdc:{stem}", ruleContent, path, source, priority)];
    }

    /// <summary>
    /// Copilot applyTo (<c>.github/copilot-instructions.md</c>) — whole-repo rule named
    /// <c>copilot</c>. Per-file <c>applyTo</c> scoping is not representable on P10's
    /// <see cref="Rule"/> (no globs field); whole-repo is the plan §13 fallback.
    /// </summary>
    public static IReadOnlyList<Rule> ParseCopilotApplyTo(string content, string path, string source, int priority)
        => [PlainRule("copilot", content, path, source, priority)];

    /// <summary>
    /// Gemini rules: <c>.gemini/rules/*.md</c> files and the config roots
    /// <c>GEMINIRULES.md</c>/<c>gemini-rules.md</c>. Name: <c>gemini:&lt;stem&gt;</c>, or
    /// <c>gemini-rules</c> for the config-root files.
    /// </summary>
    public static IReadOnlyList<Rule> ParseGeminiRules(string content, string path, string source, int priority)
    {
        var fileName = Path.GetFileName(path);
        var name = string.Equals(fileName, "GEMINIRULES.md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "gemini-rules.md", StringComparison.OrdinalIgnoreCase)
                ? "gemini-rules"
                : $"gemini:{NormalizeStem(Path.GetFileNameWithoutExtension(path))}";
        return [PlainRule(name, content, path, source, priority)];
    }

    /// <summary>GitHub rules (<c>.github/rules/*.md</c>) → <c>github:&lt;stem&gt;</c>.</summary>
    public static IReadOnlyList<Rule> ParseGithubRules(string content, string path, string source, int priority)
        => [PlainRule($"github:{NormalizeStem(Path.GetFileNameWithoutExtension(path))}", content, path, source, priority)];

    /// <summary>
    /// Repo <c>RULES.md</c> (nested <c>**/RULES.md</c>, <c>.pisharp/RULES.md</c>).
    /// Name: <c>rules</c> at the repo root (P10's nearest-project sticky surface — the
    /// provider skips that file), <c>rules:&lt;relative-dir&gt;</c> for nested files.
    /// </summary>
    public static IReadOnlyList<Rule> ParseRepoRules(string content, string path, string source, int priority, string repoRoot)
    {
        var relative = Path.GetRelativePath(repoRoot, path);
        var directory = Path.GetDirectoryName(relative) ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(directory) || directory == "."
            ? "rules"
            : $"rules:{NormalizeStem(directory)}";
        return [PlainRule(name, content, path, source, priority)];
    }

    private static Rule PlainRule(string name, string content, string path, string source, int priority)
        => new(name, content.Trim(), path, priority, RuleApplyMode.Always);

    /// <summary>
    /// Plain markdown rule file (<c>.cursor/rules/*.md</c>, <c>.github/rules/*.md</c>) →
    /// a single always-apply rule named <c>&lt;prefix&gt;:&lt;stem&gt;</c>.
    /// </summary>
    public static IReadOnlyList<Rule> ParsePlainMarkdown(string content, string path, string source, int priority, string prefix)
        => [PlainRule($"{prefix}:{NormalizeStem(Path.GetFileNameWithoutExtension(path))}", content, path, source, priority)];

    private static string NormalizeStem(string stem) => stem.Trim().TrimStart('.').ToLowerInvariant();

    private static (Dictionary<string, object?> Frontmatter, string Body) ParseMdcFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---")) return ([], normalized);
        var endIndex = normalized.IndexOf("\n---", 3);
        if (endIndex == -1) return ([], normalized);
        try
        {
            var frontmatter = MdcDeserializer.Deserialize<Dictionary<string, object?>>(normalized[4..endIndex]) ?? [];
            return (frontmatter, normalized[(endIndex + 4)..].TrimStart());
        }
        catch
        {
            return ([], normalized[(endIndex + 4)..].TrimStart());
        }
    }

    private static bool? TryGetBool(Dictionary<string, object?> frontmatter, string key)
    {
        if (!frontmatter.TryGetValue(key, out var value)) return null;
        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }
}
