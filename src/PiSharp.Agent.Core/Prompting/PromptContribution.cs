namespace PiSharp.Agent.Core.Prompting;

public enum PromptContributionSourceKind { BuiltIn, Extension, Cli, Project, User }

public sealed record PromptContributionSource(
    string Id,
    PromptContributionSourceKind Kind);

public sealed record PromptContribution(
    PromptSection Section,
    PromptContributionSource Source);
