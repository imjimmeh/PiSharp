using PiSharp.Agent.Resources.Prompting;

namespace PiSharp.Agent.Resources;

public sealed record ToolPromptInfo(
    string Name,
    string? PromptSnippet,
    IReadOnlyList<string>? PromptGuidelines = null);

public sealed record SystemPromptContextFile(string Path, string Content);

/// <summary>Legacy facade input for <see cref="SystemPromptBuilder"/>. New prompt code should use SystemPromptCompositionContext.</summary>
public sealed record SystemPromptBuildOptions(
    string Cwd,
    DateOnly? CurrentDate = null,
    IReadOnlyList<ToolPromptInfo>? Tools = null,
    IReadOnlyList<string>? SelectedToolNames = null,
    IReadOnlyList<string>? PromptGuidelines = null,
    string? CustomPrompt = null,
    string? AppendPrompt = null,
    IReadOnlyList<SystemPromptContextFile>? ContextFiles = null,
    IReadOnlyList<Skill>? Skills = null,
    IReadOnlyList<string>? SelectedSkillNames = null,
    string? ReadmePath = null,
    string? DocsPath = null,
    string? ExamplesPath = null);

public static class SystemPromptBuilder
{
    public static string Build(SystemPromptBuildOptions options)
        => SystemPromptComposer.CreateDefault().Build(SystemPromptBuildOptionsMapper.ToContext(options));
}
