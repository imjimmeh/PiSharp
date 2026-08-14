using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Extensions.Rules;

/// <summary>
/// Registers the always-apply rules (incl. the synthesized sticky RULES.md entries) as a
/// <c>rules.always</c> prompt section (plan §5.5). This is the informational surface —
/// it makes the always-apply rules visible in <c>get_system_prompt</c>; the enforcement
/// path is the message-level injection in <see cref="RulesEngine.PrepareMessagesAsync"/>.
/// </summary>
public sealed class RulesPromptContributor : IPromptContributor
{
    public const string SectionId = "rules.always";

    private readonly Func<IReadOnlyList<Rule>> _rulesProvider;

    /// <summary><paramref name="rulesProvider"/> returns the engine's current discovered rules.</summary>
    public RulesPromptContributor(Func<IReadOnlyList<Rule>> rulesProvider)
    {
        _rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
    }

    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        var alwaysRules = _rulesProvider()
            .Where(rule => rule.ApplyMode == RuleApplyMode.Always)
            .ToArray();

        if (alwaysRules.Length == 0) yield break;

        yield return new PromptContribution(
            new PromptSection(
                Id: SectionId,
                Kind: PromptSectionKind.Instructions,
                Content: new MarkdownPromptContent(Render(alwaysRules)),
                Placement: new PromptPlacement("rules", Priority: 50),
                Options: new PromptSectionOptions(OmitWhenEmpty: true)),
            new PromptContributionSource("pisharp-rules", PromptContributionSourceKind.Extension));
    }

    /// <summary>Renders each always-apply rule as a markdown section (name → content).</summary>
    public static string Render(IReadOnlyList<Rule> rules)
    {
        var lines = new List<string> { "## rules.always" };
        foreach (var rule in rules)
        {
            lines.Add($"### {rule.Name}");
            if (!string.IsNullOrWhiteSpace(rule.Path)) lines.Add($"Location: {rule.Path.Replace('\\', '/')}");
            lines.Add(string.Empty);
            lines.Add(rule.Content);
            lines.Add(string.Empty);
        }
        return string.Join("\n", lines).TrimEnd();
    }
}
