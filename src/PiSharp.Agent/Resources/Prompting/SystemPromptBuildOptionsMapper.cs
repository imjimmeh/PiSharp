using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting;

public static class SystemPromptBuildOptionsMapper
{
    private static readonly string[] DefaultSelectedTools = ["read", "bash", "edit", "write"];

    public static SystemPromptCompositionContext ToContext(SystemPromptBuildOptions options)
    {
        var selectedToolNames = options.SelectedToolNames is null ? DefaultSelectedTools : options.SelectedToolNames;
        return new SystemPromptCompositionContext(
            Cwd: options.Cwd,
            CurrentDate: options.CurrentDate ?? DateOnly.FromDateTime(DateTime.Now),
            Mode: options.CustomPrompt is null ? PromptMode.Default : PromptMode.CustomReplacement,
            Tools: (options.Tools ?? []).Select(tool => new PiSharp.Agent.Core.Prompting.ToolPromptInfo(tool.Name, tool.PromptSnippet, tool.PromptGuidelines)).ToArray(),
            SelectedToolNames: selectedToolNames,
            ExplicitGuidelines: options.PromptGuidelines ?? [],
            CustomPrompt: options.CustomPrompt,
            AppendPrompt: options.AppendPrompt,
            ContextFiles: (options.ContextFiles ?? []).Select(file => new PiSharp.Agent.Core.Prompting.SystemPromptContextFile(file.Path, file.Content)).ToArray(),
            Skills: (options.Skills ?? []).Select(skill => new PromptSkillInfo(skill.Name, skill.Description, skill.FilePath, skill.DisableModelInvocation)).ToArray(),
            DocumentationPaths: new PromptDocumentationPaths(
                options.ReadmePath ?? "README.md",
                options.DocsPath ?? "docs",
                options.ExamplesPath ?? "examples"),
            SelectedSkillNames: options.SelectedSkillNames);
    }
}
