using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

public sealed class PersonaPromptContributor : IPromptContributor
{
    private const string Persona = "You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.";

    public IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context)
    {
        if (context.Mode != PromptMode.Default) yield break;
        yield return new PromptContribution(
            new PromptSection("core.persona", PromptSectionKind.Persona, new MarkdownPromptContent(Persona), new PromptPlacement("header")),
            PromptContributorSource.BuiltIn);
    }
}
