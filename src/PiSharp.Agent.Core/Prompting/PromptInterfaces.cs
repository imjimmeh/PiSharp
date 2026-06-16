namespace PiSharp.Agent.Core.Prompting;

public interface IPromptContributor
{
    IEnumerable<PromptContribution> Contribute(SystemPromptCompositionContext context);
}

public interface IPromptTransform
{
    SystemPromptDocument Apply(SystemPromptDocument document, SystemPromptCompositionContext context);
}

public interface IPromptRenderer
{
    string Render(SystemPromptDocument document);
}

public interface ISystemPromptComposer
{
    SystemPromptDocument Compose(SystemPromptCompositionContext context);
    string Render(SystemPromptDocument document);
    string Build(SystemPromptCompositionContext context);
}
