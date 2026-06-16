namespace PiSharp.Agent.Core.Prompting;

public sealed record PromptLayout(IReadOnlyList<string> Slots)
{
    public static PromptLayout Default { get; } = new([
        "header",
        "capabilities",
        "instructions",
        "documentation",
        "user",
        "project",
        "skills",
        "environment",
        "footer"
    ]);
}
