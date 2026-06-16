using PiSharp.Agent.Core.Prompting;
using CoreToolPromptInfo = PiSharp.Agent.Core.Prompting.ToolPromptInfo;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class ToolListPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (context.Mode != PromptMode.Default) yield break;
        var byName = context.Tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visibleTools = context.SelectedToolNames
            .Select(name => byName.GetValueOrDefault(name))
            .OfType<CoreToolPromptInfo>()
            .Where(tool => !string.IsNullOrWhiteSpace(tool.PromptSnippet))
            .ToArray();
        var content = new CompositePromptContent([
            new RawPromptContent("Available tools:"),
            new ToolListPromptContent(visibleTools),
            new RawPromptContent("\nIn addition to the tools above, you may have access to other custom tools depending on the project.")
        ]);
        yield return new PromptContribution(
            new PromptSection("tools.available", PromptSectionKind.Capabilities, content, new PromptPlacement("capabilities")),
            PromptContributorSource.BuiltIn);
    }
}
