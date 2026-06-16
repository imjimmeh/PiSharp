namespace PiSharp.Agent.Core.Prompting;

public enum PromptMode { Default, CustomReplacement }

public sealed record ToolPromptInfo(
    string Name,
    string? PromptSnippet,
    IReadOnlyList<string>? PromptGuidelines = null);

public sealed record SystemPromptContextFile(string Path, string Content);

public sealed record PromptSkillInfo(
    string Name,
    string Description,
    string FilePath,
    bool DisableModelInvocation = false);

public sealed record PromptDocumentationPaths(
    string ReadmePath,
    string DocsPath,
    string ExamplesPath);

public sealed record SystemPromptCompositionContext(
    string Cwd,
    DateOnly CurrentDate,
    PromptMode Mode,
    IReadOnlyList<ToolPromptInfo> Tools,
    IReadOnlyList<string> SelectedToolNames,
    IReadOnlyList<string> ExplicitGuidelines,
    string? CustomPrompt,
    string? AppendPrompt,
    IReadOnlyList<SystemPromptContextFile> ContextFiles,
    IReadOnlyList<PromptSkillInfo> Skills,
    PromptDocumentationPaths DocumentationPaths,
    IReadOnlyList<string>? SelectedSkillNames = null);
