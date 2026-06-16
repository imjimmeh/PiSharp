using System.Text;
using PiSharp.Agent.Resources.Prompting;

namespace PiSharp.Agent.Resources;

public static class SystemPromptFormatter
{
    public static string FormatSkillsForSystemPrompt(IReadOnlyList<Skill> skills)
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToArray();
        if (visible.Length == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("The following skills provide specialized instructions for specific tasks.");
        sb.AppendLine("Read the full skill file when the task matches its description.");
        sb.AppendLine("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.");
        sb.AppendLine().AppendLine("<available_skills>");
        foreach (var skill in visible)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.FilePath)}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        return sb.ToString();
    }

    private static string EscapeXml(string value) => MarkdownSystemPromptRenderer.EscapeXml(value);
}
