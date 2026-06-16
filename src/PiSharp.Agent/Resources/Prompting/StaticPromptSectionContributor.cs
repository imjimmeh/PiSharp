using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting;

public sealed class StaticPromptSectionContributor(PromptSection section, PromptContributionSource source) : IPromptContributor
{
    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        yield return new PromptContribution(section, source);
    }
}
