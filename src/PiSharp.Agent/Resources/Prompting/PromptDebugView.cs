using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting;

public sealed record PromptDebugSection(
    string Id,
    string Slot,
    int Priority,
    string Kind,
    string SourceId);

public sealed record PromptDebugView(
    IReadOnlyList<PromptDebugSection> Sections,
    IReadOnlyList<PromptDiagnostic> Diagnostics)
{
    public static PromptDebugView FromDocument(SystemPromptDocument document)
        => new(
            document.Sections.Select(section => new PromptDebugSection(
                section.Id,
                section.Placement.Slot,
                section.Placement.Priority,
                section.Kind.ToString(),
                document.Sources?.GetValueOrDefault(section.Id)?.Id ?? string.Empty)).ToArray(),
            document.Diagnostics);
}
