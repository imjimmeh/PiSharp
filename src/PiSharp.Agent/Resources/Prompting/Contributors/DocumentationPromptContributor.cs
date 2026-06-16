using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class DocumentationPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (context.Mode != PromptMode.Default) yield break;
        var paths = context.DocumentationPaths;
        var markdown = $"""
Pi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):
- Main documentation: {paths.ReadmePath}
- Additional docs: {paths.DocsPath}
- Examples: {paths.ExamplesPath} (extensions, custom tools, SDK)
- When asked about: extensions (docs/extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), pi packages (docs/packages.md)
- When working on pi topics, read the docs and examples, and follow .md cross-references before implementing
- Always read pi .md files completely and follow links to related docs (e.g., tui.md for TUI API details)
""";
        yield return new PromptContribution(
            new PromptSection("core.documentation", PromptSectionKind.Documentation, new MarkdownPromptContent(markdown), new PromptPlacement("documentation")),
            PromptContributorSource.BuiltIn);
    }
}
