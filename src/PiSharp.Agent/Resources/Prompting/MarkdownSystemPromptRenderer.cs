using System.Text;
using PiSharp.Agent.Core.Prompting;
using CoreToolPromptInfo = PiSharp.Agent.Core.Prompting.ToolPromptInfo;

namespace PiSharp.Agent.Resources.Prompting;

public sealed class MarkdownSystemPromptRenderer : IPromptRenderer
{
    public static MarkdownSystemPromptRenderer Default { get; } = new();

    public string Render(SystemPromptDocument document)
    {
        var rendered = document.Sections
            .Select(section => RenderContent(section.Content).TrimEnd())
            .Where(text => text.Length > 0)
            .ToArray();

        return string.Join("\n\n", rendered);
    }

    public static string EscapeXml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    public static string NormalizePromptPath(string path) => path.Replace('\\', '/');

    public static string RenderContent(PromptContent content) => content switch
    {
        RawPromptContent raw => raw.Text,
        MarkdownPromptContent markdown => markdown.Markdown,
        BulletListPromptContent bullets => RenderBullets(bullets.Items),
        ToolListPromptContent tools => RenderTools(tools.Tools),
        XmlPromptContent xml => RenderXml(xml),
        CompositePromptContent composite => string.Join("\n", composite.Children.Select(RenderContent).Where(text => !string.IsNullOrEmpty(text))),
        _ => string.Empty
    };

    private static string RenderBullets(IReadOnlyList<string> items)
    {
        var lines = items.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => $"- {item.Trim()}").ToArray();
        return lines.Length == 0 ? string.Empty : string.Join("\n", lines);
    }

    private static string RenderTools(IReadOnlyList<CoreToolPromptInfo> tools)
    {
        var lines = tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name) && !string.IsNullOrWhiteSpace(tool.PromptSnippet))
            .Select(tool => $"- {tool.Name}: {tool.PromptSnippet!.Trim()}")
            .ToArray();
        return lines.Length == 0 ? "(none)" : string.Join("\n", lines);
    }

    private static string RenderXml(XmlPromptContent xml)
    {
        var attributes = xml.Attributes.Count == 0
            ? string.Empty
            : " " + string.Join(" ", xml.Attributes.Select(pair => $"{pair.Key}=\"{EscapeXml(pair.Value)}\""));
        return $"<{xml.ElementName}{attributes}>\n{xml.Body}\n</{xml.ElementName}>";
    }
}
