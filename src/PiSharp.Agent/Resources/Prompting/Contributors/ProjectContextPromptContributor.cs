using System.Text;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;
using CoreSystemPromptContextFile = PiSharp.Agent.Core.Prompting.SystemPromptContextFile;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class ProjectContextPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (context.ContextFiles.Count == 0) yield break;
        var content = context.Mode == PromptMode.CustomReplacement
            ? BuildXmlContent(context.ContextFiles)
            : BuildMarkdownContent(context.ContextFiles);
        yield return new PromptContribution(
            new PromptSection("project.context", PromptSectionKind.ProjectContext, content, new PromptPlacement("project")),
            new PromptContributionSource("project", PromptContributionSourceKind.Project));
    }

    private static PromptContent BuildMarkdownContent(IReadOnlyList<CoreSystemPromptContextFile> contextFiles)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("# Project Context");
        prompt.AppendLine();
        prompt.AppendLine("Project-specific instructions and guidelines:");
        prompt.AppendLine();
        foreach (var file in contextFiles)
        {
            prompt.AppendLine($"## {MarkdownSystemPromptRenderer.NormalizePromptPath(file.Path)}");
            prompt.AppendLine();
            prompt.AppendLine(file.Content);
            prompt.AppendLine();
        }
        return new MarkdownPromptContent(prompt.ToString());
    }

    private static PromptContent BuildXmlContent(IReadOnlyList<CoreSystemPromptContextFile> contextFiles)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine();
        prompt.AppendLine("Project-specific instructions and guidelines:");
        prompt.AppendLine();
        foreach (var file in contextFiles)
        {
            prompt.AppendLine($"<project_instructions path=\"{MarkdownSystemPromptRenderer.EscapeXml(MarkdownSystemPromptRenderer.NormalizePromptPath(file.Path))}\">");
            prompt.AppendLine(file.Content);
            prompt.AppendLine("</project_instructions>");
            prompt.AppendLine();
        }
        return new XmlPromptContent("project_context", new Dictionary<string, string>(), prompt.ToString().TrimEnd());
    }
}
