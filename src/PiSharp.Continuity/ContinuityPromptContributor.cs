using PiSharp.Agent.Core.Prompting;

using PiSharp.Continuity.Contracts;
namespace PiSharp.Continuity;

/// <summary>
/// Contributes the <c>continuity.goal</c> prompt section (kind Instructions,
/// omitted when empty — i.e. when there is no goal). Content is read live from
/// the <see cref="GoalService"/> so status/budget stay current without
/// re-registration.
/// </summary>
public sealed class ContinuityPromptContributor(GoalService goals) : IPromptContributor
{
    public const string SectionId = "continuity.goal";
    public const string SourceId = "pi:extension:continuity";

    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        var goal = goals.Goal;
        if (goal is null) yield break;
        if (goal.Status == ContinuityGoalStatus.Complete) yield break;

        var budget = goal.MaxTokens is { } m ? $"{m:N0}" : "unlimited";
        var body = string.Join('\n',
            "## Current goal",
            $"Objective: {goal.Objective}",
            $"Status: {goal.Status.ToString().ToLowerInvariant()}",
            $"Tokens used: {goal.TokensUsed:N0} / {budget}",
            string.IsNullOrWhiteSpace(goal.Progress) ? string.Empty : $"Progress: {goal.Progress}");

        yield return new PromptContribution(
            new PromptSection(
                SectionId,
                PromptSectionKind.Instructions,
                new RawPromptContent(body),
                new PromptPlacement("instructions", Priority: 90),
                new PromptSectionOptions(Protected: false, OmitWhenEmpty: true)),
            new PromptContributionSource(SourceId, PromptContributionSourceKind.Extension));
    }
}
