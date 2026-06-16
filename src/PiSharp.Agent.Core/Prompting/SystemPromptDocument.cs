namespace PiSharp.Agent.Core.Prompting;

public enum PromptSectionKind
{
    Persona,
    Capabilities,
    Instructions,
    Documentation,
    UserContent,
    ProjectContext,
    Skills,
    Environment,
    Extension
}

public sealed record PromptPlacement(
    string Slot,
    int Priority = 0,
    string? BeforeSectionId = null,
    string? AfterSectionId = null);

public sealed record PromptSectionOptions(
    bool Protected = false,
    bool OmitWhenEmpty = true);

public sealed record PromptSection(
    string Id,
    PromptSectionKind Kind,
    PromptContent Content,
    PromptPlacement Placement,
    PromptSectionOptions? Options = null);

public sealed record PromptDiagnostic(
    string Code,
    string Message,
    string? SectionId = null,
    string? SourceId = null);

public sealed record SystemPromptDocument(
    IReadOnlyList<PromptSection> Sections,
    IReadOnlyList<PromptDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, PromptContributionSource>? Sources = null);
