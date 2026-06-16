using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Extensions;

public static class PromptDocumentContentTypes
{
    public const string Raw = "raw";
    public const string Markdown = "markdown";
}

public sealed record PromptDocumentSectionDto(
    string Id,
    string Kind,
    string Slot,
    int Priority,
    string ContentType,
    string Content,
    bool Protected = false,
    string? BeforeSectionId = null,
    string? AfterSectionId = null,
    string? SourceId = null);

public sealed record PromptDocumentSectionPatch(
    string Id,
    string Content,
    string Slot = "instructions",
    int Priority = 0,
    string Kind = "extension",
    string ContentType = PromptDocumentContentTypes.Markdown,
    bool Protected = false,
    string? BeforeSectionId = null,
    string? AfterSectionId = null);

public sealed record PromptDocumentPatch(
    IReadOnlyList<string>? RemoveSectionIds = null,
    IReadOnlyList<PromptDocumentSectionPatch>? ReplaceSections = null,
    IReadOnlyList<PromptDocumentSectionPatch>? AppendSections = null);

public sealed record PromptDocumentHookPayload(
    string Prompt,
    IReadOnlyList<PromptDocumentSectionDto> Sections,
    IReadOnlyList<PromptDiagnostic> Diagnostics,
    object Resources);
