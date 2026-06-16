using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class AppendPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.AppendPrompt)) yield break;
        yield return new PromptContribution(
            new PromptSection("user.append", PromptSectionKind.UserContent, new RawPromptContent(context.AppendPrompt), new PromptPlacement("user")),
            new PromptContributionSource("user", PromptContributionSourceKind.User));
    }
}
