namespace PiSharp.Agent.Core.Prompting;

public abstract record PromptContent;

public sealed record RawPromptContent(string Text) : PromptContent;

public sealed record MarkdownPromptContent(string Markdown) : PromptContent;

public sealed record BulletListPromptContent(IReadOnlyList<string> Items) : PromptContent;

public sealed record ToolListPromptContent(IReadOnlyList<ToolPromptInfo> Tools) : PromptContent;

public sealed record XmlPromptContent(string ElementName, IReadOnlyDictionary<string, string> Attributes, string Body) : PromptContent;

public sealed record CompositePromptContent(IReadOnlyList<PromptContent> Children) : PromptContent;
