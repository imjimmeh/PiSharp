using System.Text.RegularExpressions;
using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// <summary>
/// Immutable permission policy compiled from the <c>extensions.pisharp-permissions.*</c>
/// settings (P29 §8). Evaluation follows the allow/deny/ask matrix with
/// most-restrictive-wins precedence (deny &gt; ask &gt; allow), then the dangerous-default
/// table, then the <c>mode</c> posture for <c>ask</c> resolution. Explicit session grants are
/// layered on top by the middleware (grant wins immediately, P29 §3.3).
/// </summary>
public sealed class PermissionsPolicy
{
    public const string ModePrompt = "prompt";
    public const string ModeAutomatic = "automatic";
    public const string ModeStrict = "strict";

    public const double DefaultGrantTtlSeconds = 3600;
    public const bool DefaultHeadlessDeny = true;
    public const bool DefaultAudit = true;

    private static readonly PermissionRule[] EmptyRules = [];

    private readonly (PermissionRule Rule, Regex? Pattern)[] _allow;
    private readonly (PermissionRule Rule, Regex? Pattern)[] _deny;
    private readonly (PermissionRule Rule, Regex? Pattern)[] _ask;

    public PermissionsPolicy(
        string mode,
        IReadOnlyList<PermissionRule> allow,
        IReadOnlyList<PermissionRule> deny,
        IReadOnlyList<PermissionRule> ask,
        bool headlessDeny = DefaultHeadlessDeny,
        double grantTtlSeconds = DefaultGrantTtlSeconds,
        bool audit = DefaultAudit)
    {
        Mode = string.IsNullOrWhiteSpace(mode) ? ModePrompt : mode.ToLowerInvariant();
        AllowRules = allow ?? [];
        DenyRules = deny ?? [];
        AskRules = ask ?? [];
        HeadlessDeny = headlessDeny;
        GrantTtlSeconds = grantTtlSeconds > 0 ? grantTtlSeconds : DefaultGrantTtlSeconds;
        Audit = audit;

        _allow = Compile(AllowRules);
        _deny = Compile(DenyRules);
        _ask = Compile(AskRules);
    }

    public string Mode { get; }
    public IReadOnlyList<PermissionRule> AllowRules { get; }
    public IReadOnlyList<PermissionRule> DenyRules { get; }
    public IReadOnlyList<PermissionRule> AskRules { get; }
    public bool HeadlessDeny { get; }
    public double GrantTtlSeconds { get; }
    public bool Audit { get; }

    public bool IsAutomatic => Mode == ModeAutomatic;
    public bool IsStrict => Mode == ModeStrict;

    public static PermissionsPolicy Default { get; } =
        new(ModePrompt, EmptyRules, EmptyRules, EmptyRules);

    /// <summary>
    /// Reads the <c>extensions.pisharp-permissions.*</c> settings and compiles the policy.
    /// Throws when a rule pattern is not a valid regex so a misconfigured deny/ask rule can
    /// never be silently skipped (that would loosen the gate).
    /// </summary>
    public static PermissionsPolicy Load(IExtensionApi api)
    {
        var settings = api.Settings;
        var mode = settings.Get<string>("mode") ?? ModePrompt;
        var allow = settings.Get<List<PermissionRule>>("allow") ?? [];
        var deny = settings.Get<List<PermissionRule>>("deny") ?? [];
        var ask = settings.Get<List<PermissionRule>>("ask") ?? [];
        var headlessDeny = settings.Get<bool?>("headlessDeny") ?? DefaultHeadlessDeny;
        var ttl = settings.Get<double?>("grantTtlSeconds") ?? DefaultGrantTtlSeconds;
        var audit = settings.Get<bool?>("audit") ?? DefaultAudit;
        return new PermissionsPolicy(mode, allow, deny, ask, headlessDeny, ttl, audit);
    }

    /// <summary>
    /// Evaluates a tool call: static matrix (most restrictive wins), then the dangerous
    /// default for <paramref name="dangerousCategory"/>, then the <c>ask</c> resolution
    /// posture (<c>automatic</c> allows, headless denies per <see cref="HeadlessDeny"/>,
    /// otherwise the decision stays Ask for the middleware to prompt).
    /// </summary>
    public PermissionDecision Evaluate(string tool, string serializedArgs, string dangerousCategory, bool headless)
    {
        var matched = ResolveMatrix(tool, serializedArgs);
        if (matched is not null)
        {
            var action = matched.Value.Action;
            if (action == PermissionAction.Ask)
                return ResolveAsk(new PermissionDecision(PermissionAction.Ask, ReasonForRule(matched.Value.Rule), DescribeRule(matched.Value.Rule)), headless);
            return new PermissionDecision(action, ReasonForRule(matched.Value.Rule), DescribeRule(matched.Value.Rule));
        }

        if (IsStrict)
        {
            return new PermissionDecision(
                PermissionAction.Deny,
                $"Tool '{tool}' is not allow-listed and strict mode denies unlisted tools.",
                "mode=strict");
        }

        var (defaultAction, defaultReason) = dangerousCategory switch
        {
            DangerousOpDetector.WriteOutsideCwd => (PermissionAction.Deny,
                $"Tool '{tool}' targets a path outside the working directory; writes outside cwd are denied."),
            DangerousOpDetector.WriteOverwrite => (PermissionAction.Ask,
                $"Tool '{tool}' overwrites an existing file; overwrites require approval."),
            DangerousOpDetector.GitPush => (PermissionAction.Ask,
                "Bash command contains a destructive git operation (push / reset --hard / rm -rf); approval required."),
            DangerousOpDetector.Bash => (PermissionAction.Ask,
                "Bash execution requires approval (dangerous by default)."),
            _ => (PermissionAction.Allow, $"Tool '{tool}' is not restricted.")
        };

        return ResolveAsk(new PermissionDecision(defaultAction, defaultReason, $"dangerous-default:{dangerousCategory}"), headless);
    }

    /// <summary>
    /// Resolves an Ask decision through the mode posture. Returns the decision unchanged when
    /// the result is still Ask (interactive prompt required).
    /// </summary>
    public PermissionDecision ResolveAsk(PermissionDecision decision, bool headless)
    {
        if (decision.Action != PermissionAction.Ask) return decision;

        if (IsAutomatic)
        {
            return new PermissionDecision(
                PermissionAction.Allow,
                $"Automatic mode allows the otherwise-ask tool call: {decision.Reason}",
                decision.MatchedRule);
        }

        if (headless)
        {
            return HeadlessDeny
                ? new PermissionDecision(
                    PermissionAction.Deny,
                    $"No interactive UI attached; ask auto-denied (headlessDeny): {decision.Reason}",
                    decision.MatchedRule)
                : new PermissionDecision(
                    PermissionAction.Allow,
                    $"No interactive UI attached and headlessDeny is disabled; ask auto-allowed: {decision.Reason}",
                    decision.MatchedRule);
        }

        return decision;
    }

    private (PermissionRule Rule, PermissionAction Action)? ResolveMatrix(string tool, string serializedArgs)
    {
        if (Matches(_deny, tool, serializedArgs, out var denyRule)) return (denyRule!, PermissionAction.Deny);
        if (Matches(_ask, tool, serializedArgs, out var askRule)) return (askRule!, PermissionAction.Ask);
        if (Matches(_allow, tool, serializedArgs, out var allowRule)) return (allowRule!, PermissionAction.Allow);
        return null;
    }

    private static bool Matches(
        (PermissionRule Rule, Regex? Pattern)[] rules,
        string tool,
        string serializedArgs,
        out PermissionRule? matched)
    {
        foreach (var entry in rules)
        {
            if (!string.Equals(entry.Rule.Tool, tool, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Pattern is null || entry.Pattern.IsMatch(serializedArgs))
            {
                matched = entry.Rule;
                return true;
            }
        }
        matched = null;
        return false;
    }

    private static (PermissionRule Rule, Regex? Pattern)[] Compile(IReadOnlyList<PermissionRule> rules)
    {
        var compiled = new (PermissionRule, Regex?)[rules.Count];
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            compiled[i] = (rule, CompilePattern(rule.Pattern, rule.Tool));
        }
        return compiled;
    }

    private static Regex? CompilePattern(string? pattern, string tool)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Invalid pattern '{pattern}' on permission rule for tool '{tool}': {ex.Message}", ex);
        }
    }

    private static string ReasonForRule(PermissionRule rule)
    {
        var description = rule.Pattern is null
            ? $"rule '{rule.Tool}'"
            : $"rule '{rule.Tool}' matching pattern '{rule.Pattern}'";
        return $"Permission {description}.";
    }

    private static string DescribeRule(PermissionRule rule)
        => rule.Pattern is null ? $"rule:{rule.Tool}" : $"rule:{rule.Tool}:{rule.Pattern}";
}
