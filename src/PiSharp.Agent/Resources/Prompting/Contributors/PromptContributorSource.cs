using PiSharp.Agent.Core.Prompting;

namespace PiSharp.Agent.Resources.Prompting.Contributors;

internal static class PromptContributorSource
{
    public static PromptContributionSource BuiltIn { get; } = new("core", PromptContributionSourceKind.BuiltIn);
}
