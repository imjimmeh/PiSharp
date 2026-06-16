using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting;

public sealed class PromptDocumentAssembler(PromptLayout? layout = null)
{
    private readonly PromptLayout _layout = layout ?? PromptLayout.Default;

    public SystemPromptDocument Assemble(IEnumerable<PromptContribution> contributions)
    {
        var sections = new Dictionary<string, PromptContribution>(StringComparer.Ordinal);
        var diagnostics = new List<PromptDiagnostic>();

        foreach (var contribution in contributions)
        {
            if (!sections.TryGetValue(contribution.Section.Id, out var existing))
            {
                sections[contribution.Section.Id] = contribution;
                continue;
            }

            if (StringComparer.Ordinal.Equals(existing.Source.Id, contribution.Source.Id))
            {
                sections[contribution.Section.Id] = contribution;
                continue;
            }

            var winner = CompareContributions(existing, contribution) <= 0 ? existing : contribution;
            sections[contribution.Section.Id] = winner;
            diagnostics.Add(new PromptDiagnostic(
                "duplicate_section",
                $"Section '{contribution.Section.Id}' was contributed by both '{existing.Source.Id}' and '{contribution.Source.Id}'. Kept '{winner.Source.Id}'.",
                contribution.Section.Id,
                contribution.Source.Id));
        }

        var ordered = Order(sections.Values.Select(value => value.Section)).ToArray();
        var sources = sections.Values.ToDictionary(value => value.Section.Id, value => value.Source, StringComparer.Ordinal);
        return new SystemPromptDocument(ordered, diagnostics, sources);
    }

    public SystemPromptDocument Order(SystemPromptDocument document)
        => document with { Sections = Order(document.Sections).ToArray() };

    private IEnumerable<PromptSection> Order(IEnumerable<PromptSection> sections)
    {
        var slotIndexes = _layout.Slots.Select((slot, index) => (slot, index)).ToDictionary(pair => pair.slot, pair => pair.index, StringComparer.Ordinal);
        var ordered = sections
            .OrderBy(section => slotIndexes.GetValueOrDefault(section.Placement.Slot, int.MaxValue))
            .ThenBy(section => section.Placement.Priority)
            .ThenBy(section => section.Id, StringComparer.Ordinal)
            .ToList();

        ApplyRelativePlacement(ordered);
        return ordered;
    }

    private static void ApplyRelativePlacement(List<PromptSection> sections)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (!string.IsNullOrWhiteSpace(section.Placement.BeforeSectionId))
            {
                var target = sections.FindIndex(s => StringComparer.Ordinal.Equals(s.Id, section.Placement.BeforeSectionId));
                if (target >= 0 && target != i)
                {
                    sections.RemoveAt(i);
                    if (target > i) target--;
                    sections.Insert(target, section);
                    i = -1;
                }
            }
            else if (!string.IsNullOrWhiteSpace(section.Placement.AfterSectionId))
            {
                var target = sections.FindIndex(s => StringComparer.Ordinal.Equals(s.Id, section.Placement.AfterSectionId));
                if (target >= 0 && target != i - 1)
                {
                    sections.RemoveAt(i);
                    if (target > i) target--;
                    sections.Insert(target + 1, section);
                    i = -1;
                }
            }
        }
    }

    private static int CompareContributions(PromptContribution left, PromptContribution right)
    {
        var sourceKind = left.Source.Kind.CompareTo(right.Source.Kind);
        if (sourceKind != 0) return sourceKind;
        return string.CompareOrdinal(left.Source.Id, right.Source.Id);
    }
}
