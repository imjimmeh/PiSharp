using PiSharp.Agent.Core.Prompting;
using PiSharp.Memory.Abstractions;

namespace PiSharp.Memory;

/// <summary>
/// Injects stored mental models into the first prompt of a session as a
/// <see cref="PromptSectionKind.ProjectContext"/> section. The core plugin
/// refreshes the record cache on each <c>agent_start</c>; this contributor owns
/// the per-session "already injected" flag and marks it only when it actually
/// yields a section, so the injection happens at most once per session and
/// never on later turns — <c>recall</c> is the query surface.
/// </summary>
public sealed class MentalModelPromptContributor : IPromptContributor
{
    public const string SectionId = "pisharp-memory.mental-models";

    private readonly Func<IReadOnlyList<MemoryRecord>?> _recordsProvider;
    private readonly Func<bool> _hasInjectedThisSession;
    private readonly Action _markInjected;

    public MentalModelPromptContributor(
        Func<IReadOnlyList<MemoryRecord>?> recordsProvider,
        Func<bool> hasInjectedThisSession,
        Action markInjected)
    {
        _recordsProvider = recordsProvider ?? throw new ArgumentNullException(nameof(recordsProvider));
        _hasInjectedThisSession = hasInjectedThisSession ?? throw new ArgumentNullException(nameof(hasInjectedThisSession));
        _markInjected = markInjected ?? throw new ArgumentNullException(nameof(markInjected));
    }

    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (_hasInjectedThisSession()) yield break;

        var records = _recordsProvider();
        if (records is null || records.Count == 0) yield break;

        _markInjected();
        yield return new PromptContribution(
            new PromptSection(
                Id: SectionId,
                Kind: PromptSectionKind.ProjectContext,
                Content: new MarkdownPromptContent(Render(records)),
                Placement: new PromptPlacement("project-context", Priority: 200),
                Options: new PromptSectionOptions(OmitWhenEmpty: true)),
            new PromptContributionSource("pisharp-memory", PromptContributionSourceKind.Extension));
    }

    /// <summary>Renders records in the plan's mental-model markdown shape.</summary>
    public static string Render(IReadOnlyList<MemoryRecord> records)
    {
        var lines = new List<string> { "## Project memory (mental models)" };
        foreach (var record in records)
        {
            var oneLine = record.Content.Replace('\n', ' ').Trim();
            lines.Add($"- [{record.RecordKey}] {record.Title}: {oneLine} (updated {record.UpdatedAt:yyyy-MM-dd})");
        }
        return string.Join("\n", lines);
    }
}
