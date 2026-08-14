namespace PiSharp.Extensions.Rules;

/// <summary>
/// Builds the always-apply reminder text for a rule, mirroring the
/// <c>Skill.FormatInvocation</c> wrapper shape (plan §7: <c>&lt;rules name=... location=...&gt;</c>).
/// Used both for the per-turn message-level injection and the sticky RULES.md reminder.
/// </summary>
public static class StickyRulesInjector
{
    /// <summary>
    /// Wraps a rule's content in a <c>&lt;rules&gt;</c> block. <paramref name="location"/>
    /// is the source path (file, or a synthetic label for synthesized rules).
    /// </summary>
    public static string Format(Rule rule, string? location = null)
    {
        var loc = location ?? rule.Path ?? "/";
        var normalized = loc.Replace('\\', '/');
        return $"<rules name=\"{rule.Name}\" location=\"{normalized}\">\n\n{rule.Content}\n</rules>";
    }

    /// <summary>Wraps a synthesized sticky RULES.md entry.</summary>
    public static string FormatSticky(string name, string content, string? location = null)
        => Format(new Rule(name, content, Path: location, ApplyMode: RuleApplyMode.Always), location);
}
