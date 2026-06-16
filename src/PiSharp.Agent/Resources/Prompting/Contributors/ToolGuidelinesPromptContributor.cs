using PiSharp.Agent.Core.Prompting;
using CoreToolPromptInfo = PiSharp.Agent.Core.Prompting.ToolPromptInfo;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class ToolGuidelinesPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (context.Mode != PromptMode.Default) yield break;
        var selectedTools = new HashSet<string>(context.SelectedToolNames, StringComparer.Ordinal);
        var guidelines = BuildGuidelines(context.Tools, selectedTools, context.ExplicitGuidelines);
        var content = new CompositePromptContent([
            new RawPromptContent("Guidelines:"),
            new BulletListPromptContent(guidelines)
        ]);
        yield return new PromptContribution(
            new PromptSection("tools.guidelines", PromptSectionKind.Instructions, content, new PromptPlacement("instructions")),
            PromptContributorSource.BuiltIn);
    }

    private static IReadOnlyList<string> BuildGuidelines(
        IReadOnlyList<CoreToolPromptInfo> tools,
        HashSet<string> selectedTools,
        IReadOnlyList<string> explicitGuidelines)
    {
        var guidelines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? guideline)
        {
            var normalized = guideline?.Trim();
            if (string.IsNullOrEmpty(normalized) || !seen.Add(normalized)) return;
            guidelines.Add(normalized);
        }

        var hasBash = selectedTools.Contains("bash");
        var hasGrep = selectedTools.Contains("grep");
        var hasFind = selectedTools.Contains("find");
        var hasLs = selectedTools.Contains("ls");
        if (hasBash && !hasGrep && !hasFind && !hasLs) Add("Use bash for file operations like ls, rg, find");
        else if (hasBash && (hasGrep || hasFind || hasLs)) Add("Prefer grep/find/ls tools over bash for file exploration (faster, respects .gitignore)");

        foreach (var tool in tools.Where(tool => selectedTools.Contains(tool.Name)))
        {
            foreach (var guideline in tool.PromptGuidelines ?? []) Add(guideline);
        }

        foreach (var guideline in explicitGuidelines) Add(guideline);
        Add("Be concise in your responses");
        Add("Show file paths clearly when working with files");
        return guidelines;
    }
}
