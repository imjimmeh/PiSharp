using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;
using Xunit;

namespace PiSharp.Agent.Tests.Resources.Prompting;

public sealed class MarkdownSystemPromptRendererTests
{
    [Fact]
    public void RenderUsesStableBlankLineBetweenSections()
    {
        var document = new SystemPromptDocument([
            Section("a", new RawPromptContent("A")),
            Section("b", new RawPromptContent("B"))
        ], []);

        var rendered = MarkdownSystemPromptRenderer.Default.Render(document);

        Assert.Equal("A\n\nB", rendered);
    }

    [Fact]
    public void RenderEscapesXmlAttributes()
    {
        var document = new SystemPromptDocument([
            Section("xml", new XmlPromptContent("item", new Dictionary<string, string> { ["path"] = "a&b\"c" }, "body"))
        ], []);

        var rendered = MarkdownSystemPromptRenderer.Default.Render(document);

        Assert.Contains("path=\"a&amp;b&quot;c\"", rendered);
    }

    [Fact]
    public void RenderToolListShowsNoneWhenEmpty()
    {
        var document = new SystemPromptDocument([Section("tools", new ToolListPromptContent([]))], []);

        Assert.Equal("(none)", MarkdownSystemPromptRenderer.Default.Render(document));
    }

    private static PromptSection Section(string id, PromptContent content)
        => new(id, PromptSectionKind.Extension, content, new PromptPlacement("header"));
}
