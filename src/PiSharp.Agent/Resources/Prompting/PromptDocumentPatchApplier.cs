using PiSharp.Agent.Core.Prompting;
using PiSharp.Extensions;

namespace PiSharp.Agent.Resources.Prompting;

public sealed class PromptDocumentPatchApplier(PromptLayout? layout = null)
{
    private readonly PromptDocumentAssembler _assembler = new(layout ?? PromptLayout.Default);

    public SystemPromptDocument Apply(SystemPromptDocument document, PromptDocumentPatch? patch, PromptContributionSource? source = null)
    {
        if (patch is null) return document;

        var before = document;
        var sections = document.Sections.ToDictionary(section => section.Id, section => section, StringComparer.Ordinal);
        var sources = document.Sources is null
            ? new Dictionary<string, PromptContributionSource>(StringComparer.Ordinal)
            : new Dictionary<string, PromptContributionSource>(document.Sources, StringComparer.Ordinal);
        var diagnostics = document.Diagnostics.ToList();

        foreach (var id in patch.RemoveSectionIds ?? [])
        {
            sections.Remove(id);
            sources.Remove(id);
        }

        foreach (var replacement in patch.ReplaceSections ?? [])
        {
            sections[replacement.Id] = ToSection(replacement);
            if (source is not null) sources[replacement.Id] = source;
        }

        foreach (var appended in patch.AppendSections ?? [])
        {
            if (sections.ContainsKey(appended.Id))
            {
                diagnostics.Add(new PromptDiagnostic(
                    "prompt_document_duplicate_append",
                    $"Prompt document patch tried to append section '{appended.Id}', but that id already exists. Kept the existing section.",
                    appended.Id,
                    source?.Id));
                continue;
            }

            sections[appended.Id] = ToSection(appended);
            if (source is not null) sources[appended.Id] = source;
        }

        var after = _assembler.Order(document with { Sections = sections.Values.ToArray(), Diagnostics = diagnostics, Sources = sources });
        var protectedDiagnostics = ProtectedSectionRemovalDiagnostics(before, after).ToArray();
        return protectedDiagnostics.Length == 0 ? after : after with { Diagnostics = after.Diagnostics.Concat(protectedDiagnostics).ToArray() };
    }

    public IReadOnlyList<PromptDocumentSectionDto> ToSectionDtos(SystemPromptDocument document)
        => document.Sections.Select(section => ToDto(section, document.Sources?.GetValueOrDefault(section.Id))).ToArray();

    private static PromptDocumentSectionDto ToDto(PromptSection section, PromptContributionSource? source)
        => new(
            section.Id,
            KindToString(section.Kind),
            section.Placement.Slot,
            section.Placement.Priority,
            ContentType(section.Content),
            MarkdownSystemPromptRenderer.RenderContent(section.Content),
            section.Options?.Protected == true,
            section.Placement.BeforeSectionId,
            section.Placement.AfterSectionId,
            source?.Id);

    private static PromptSection ToSection(PromptDocumentSectionPatch patch)
        => new(
            patch.Id,
            ParseKind(patch.Kind),
            ToContent(patch.ContentType, patch.Content),
            new PromptPlacement(patch.Slot, patch.Priority, patch.BeforeSectionId, patch.AfterSectionId),
            new PromptSectionOptions(Protected: patch.Protected));

    private static PromptContent ToContent(string contentType, string content)
        => string.Equals(contentType, PromptDocumentContentTypes.Raw, StringComparison.OrdinalIgnoreCase)
            ? new RawPromptContent(content)
            : new MarkdownPromptContent(content);

    private static string ContentType(PromptContent content)
        => content is RawPromptContent ? PromptDocumentContentTypes.Raw : PromptDocumentContentTypes.Markdown;

    private static PromptSectionKind ParseKind(string kind)
        => Enum.TryParse<PromptSectionKind>(kind, ignoreCase: true, out var parsed) ? parsed : PromptSectionKind.Extension;

    private static string KindToString(PromptSectionKind kind)
        => kind.ToString().ToLowerInvariant();

    private static IEnumerable<PromptDiagnostic> ProtectedSectionRemovalDiagnostics(SystemPromptDocument before, SystemPromptDocument after)
    {
        var afterIds = after.Sections.Select(section => section.Id).ToHashSet(StringComparer.Ordinal);
        return before.Sections
            .Where(section => section.Options?.Protected == true && !afterIds.Contains(section.Id))
            .Select(section => new PromptDiagnostic(
                "protected_section_removed",
                $"Protected section '{section.Id}' was removed by a prompt document patch.",
                section.Id,
                before.Sources?.GetValueOrDefault(section.Id)?.Id));
    }
}
