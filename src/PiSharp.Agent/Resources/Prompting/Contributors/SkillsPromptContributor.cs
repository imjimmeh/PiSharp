using System.Text;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class SkillsPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (!context.SelectedToolNames.Contains("read", StringComparer.Ordinal) || context.Skills.Count == 0) yield break;
        var visible = context.Skills.Where(skill => !skill.DisableModelInvocation).ToArray();
        if (context.SelectedSkillNames is not null)
        {
            var selected = context.SelectedSkillNames.ToHashSet(StringComparer.Ordinal);
            visible = visible.Where(skill => selected.Contains(skill.Name)).ToArray();
        }
        if (visible.Length == 0) yield break;
        yield return new PromptContribution(
            new PromptSection("skills.available", PromptSectionKind.Skills, new RawPromptContent(FormatSkills(visible)), new PromptPlacement("skills")),
            PromptContributorSource.BuiltIn);
    }

    private static string FormatSkills(IReadOnlyList<PromptSkillInfo> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The following skills provide specialized instructions for specific tasks.");
        sb.AppendLine("Read the full skill file when the task matches its description.");
        sb.AppendLine("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.");
        sb.AppendLine().AppendLine("<available_skills>");
        foreach (var skill in skills)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{MarkdownSystemPromptRenderer.EscapeXml(skill.Name)}</name>");
            sb.AppendLine($"    <description>{MarkdownSystemPromptRenderer.EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{MarkdownSystemPromptRenderer.EscapeXml(skill.FilePath)}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        return sb.ToString();
    }
}
