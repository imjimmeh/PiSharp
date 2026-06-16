using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class EnvironmentPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        yield return new PromptContribution(
            new PromptSection(
                "environment.current-date",
                PromptSectionKind.Environment,
                new RawPromptContent($"Current date: {context.CurrentDate:yyyy-MM-dd}"),
                new PromptPlacement("environment"),
                new PromptSectionOptions(Protected: true)),
            PromptContributorSource.BuiltIn);
        yield return new PromptContribution(
            new PromptSection(
                "environment.cwd",
                PromptSectionKind.Environment,
                new RawPromptContent($"Current working directory: {MarkdownSystemPromptRenderer.NormalizePromptPath(context.Cwd)}"),
                new PromptPlacement("environment", Priority: 10),
                new PromptSectionOptions(Protected: true)),
            PromptContributorSource.BuiltIn);
    }
}
