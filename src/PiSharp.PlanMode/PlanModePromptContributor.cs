using PiSharp.Agent.Core.Prompting;

namespace PiSharp.PlanMode;

/// <summary>
/// Contributes the plan-mode prompt sections: read-only planning instructions while
/// <see cref="PlanModePhase.Planning"/>, and the approved plan body (plan-protection scope)
/// while <see cref="PlanModePhase.Executing"/>. Content is read live from the service
/// (no re-registration on transition).
/// </summary>
public sealed class PlanModePromptContributor(PlanModeService service) : IPromptContributor
{
    public const string PlanningSectionId = "plan-mode";
    public const string ExecutingSectionId = "plan-mode-approved-plan";
    public const string SourceId = "pi:extension:plan-mode";

    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        switch (service.Phase)
        {
            case PlanModePhase.Planning:
                yield return new PromptContribution(
                    new PromptSection(
                        PlanningSectionId,
                        PromptSectionKind.Instructions,
                        new RawPromptContent(BuildPlanningInstructions()),
                        new PromptPlacement("instructions", Priority: 100),
                        new PromptSectionOptions(Protected: false)),
                    new PromptContributionSource(SourceId, PromptContributionSourceKind.Extension));
                break;

            case PlanModePhase.Executing when !string.IsNullOrWhiteSpace(service.LastPlanBody):
                yield return new PromptContribution(
                    new PromptSection(
                        ExecutingSectionId,
                        PromptSectionKind.Instructions,
                        new RawPromptContent(BuildExecutingInstructions(service.LastPlanBody!)),
                        new PromptPlacement("instructions", Priority: 100),
                        new PromptSectionOptions(Protected: false)),
                    new PromptContributionSource(SourceId, PromptContributionSourceKind.Extension));
                break;
        }
    }

    private string BuildPlanningInstructions()
    {
        var state = service.State;
        return string.Join('\n',
            "## Plan mode",
            $"You are in the read-only planning phase. Active tool set: {string.Join(", ", state.RestrictedToolNames)}.",
            "You MUST NOT modify any file or run state-changing commands; explore, then end with a complete plan as your final message.",
            state.PlanningModel is null ? string.Empty : $"Planning model: {state.PlanningModel}.",
            $"The plan will be written to: {state.PlanFile}.",
            "End your turn with the complete plan as your final message so it can be captured and approved.");
    }

    private static string BuildExecutingInstructions(string planBody)
        => string.Join('\n',
            "## Approved plan",
            "Work must stay within the approved plan below:",
            string.Empty,
            planBody.TrimEnd('\r', '\n'));
}
