using PiSharp.Agent.Core.Prompting;
using PiSharp.ContinualHarness.Contracts;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Contributes <c>harness.prompt.&lt;name&gt;</c> sections from effective prompt-kind entries
/// (local overrides global by construction of the supplied entry sequence). Kind/naming discipline:
/// the base prompt document is never rewritten — only supplemental sections are injected.
/// </summary>
public sealed class HarnessPromptContributor(Func<IEnumerable<HarnessEntry>> getEntries) : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        foreach (var entry in getEntries() ?? [])
        {
            if (entry.Key.Kind != HarnessRefinementKind.Prompt) continue;

            var markdown = entry.Content.ValueKind == System.Text.Json.JsonValueKind.Object
                           && entry.Content.TryGetProperty("markdown", out var m)
                ? m.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(markdown)) continue;

            var slot = entry.Content.TryGetProperty("slot", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                ? s.GetString()
                : "instructions";

            var priority = 0;
            if (entry.Content.TryGetProperty("priority", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number)
                priority = p.GetInt32();

            yield return new PromptContribution(
                new PromptSection(
                    Id: $"harness.prompt.{entry.Key.Name}",
                    Kind: MapSlot(slot),
                    Content: new RawPromptContent(markdown),
                    Placement: new PromptPlacement(slot ?? "instructions", priority)),
                new PromptContributionSource("pisharp-refine", PromptContributionSourceKind.Extension));
        }
    }

    public static PromptSectionKind MapSlot(string? slot)
        => slot switch
        {
            "persona" => PromptSectionKind.Persona,
            "capabilities" => PromptSectionKind.Capabilities,
            "project-context" or "project_context" => PromptSectionKind.ProjectContext,
            "documentation" => PromptSectionKind.Documentation,
            "skills" => PromptSectionKind.Skills,
            "environment" => PromptSectionKind.Environment,
            _ => PromptSectionKind.Instructions,
        };
}
