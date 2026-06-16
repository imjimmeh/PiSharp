using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class CustomPromptContributor : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (context.Mode != PromptMode.CustomReplacement) yield break;
        yield return new PromptContribution(
            new PromptSection("user.custom-system-prompt", PromptSectionKind.UserContent, new RawPromptContent(context.CustomPrompt ?? string.Empty), new PromptPlacement("header"), new PromptSectionOptions(OmitWhenEmpty: false)),
            new PromptContributionSource("user", PromptContributionSourceKind.User));
    }
}
