using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Resources.Prompting.Contributors;

namespace PiSharp.Agent.Resources.Prompting;

public sealed class SystemPromptComposer(
    IReadOnlyList<IPromptContributor> contributors,
    IReadOnlyList<IPromptTransform>? transforms = null,
    PromptLayout? layout = null,
    IPromptRenderer? renderer = null) : ISystemPromptComposer
{
    private readonly PromptDocumentAssembler _assembler = new(layout ?? PromptLayout.Default);

    public static SystemPromptComposer CreateDefault(
        IEnumerable<IPromptContributor>? additionalContributors = null,
        IEnumerable<IPromptTransform>? transforms = null,
        PromptLayout? layout = null,
        IPromptRenderer? renderer = null)
    {
        var contributors = BuiltInContributors().Concat(additionalContributors ?? []).ToArray();
        return new SystemPromptComposer(contributors, (transforms ?? []).ToArray(), layout, renderer);
    }

    public SystemPromptDocument Compose(SystemPromptCompositionContext context)
    {
        var contributions = contributors.SelectMany(contributor => contributor.Contribute(context));
        var document = _assembler.Assemble(contributions);
        foreach (var transform in transforms ?? [])
        {
            var before = document;
            document = transform.Apply(document, context);
            document = CheckProtectedSectionRemoval(before, document);
            document = _assembler.Order(document);
        }

        return _assembler.Order(document);
    }

    public string Render(SystemPromptDocument document)
        => (renderer ?? MarkdownSystemPromptRenderer.Default).Render(document);

    public string Build(SystemPromptCompositionContext context)
        => Render(Compose(context));

    private static IReadOnlyList<IPromptContributor> BuiltInContributors() =>
    [
        new PersonaPromptContributor(),
        new CustomPromptContributor(),
        new ToolListPromptContributor(),
        new ToolGuidelinesPromptContributor(),
        new DocumentationPromptContributor(),
        new AppendPromptContributor(),
        new ProjectContextPromptContributor(),
        new SkillsPromptContributor(),
        new EnvironmentPromptContributor()
    ];

    private static SystemPromptDocument CheckProtectedSectionRemoval(SystemPromptDocument before, SystemPromptDocument after)
    {
        var afterIds = after.Sections.Select(section => section.Id).ToHashSet(StringComparer.Ordinal);
        var removed = before.Sections
            .Where(section => section.Options?.Protected == true && !afterIds.Contains(section.Id))
            .Select(section => new PromptDiagnostic("protected_section_removed", $"Protected section '{section.Id}' was removed by a transform.", section.Id, before.Sources?.GetValueOrDefault(section.Id)?.Id))
            .ToArray();
        if (removed.Length == 0) return after;
        return after with { Diagnostics = after.Diagnostics.Concat(removed).ToArray() };
    }
}
