namespace PiSharp.Extensions.Rules;

/// <summary>
/// Plugin-owned configuration for the rules engine (P10 plan §9). v1 reads the
/// <c>--no-rules</c> / <c>--no-sticky-rules</c> extension flags (no P02 settings
/// dependency); the reserved P02 keys (<c>extensions.pisharp-rules.*</c>) are the
/// future settings path once <see cref="IExtensionApi.Settings"/> lands.
/// </summary>
public sealed record RulesOptions
{
    /// <summary>Discovery roots for <c>rules/*.md</c> / <c>rules/*.mdc</c> files, nearest-wins per name.</summary>
    public IReadOnlyList<string> RuleRoots { get; init; } = [];

    /// <summary>User-level sticky file, e.g. <c>~/.pi/agent/RULES.md</c>.</summary>
    public string? UserStickyRulesPath { get; init; }

    /// <summary>Project/session working directory for the ancestor RULES.md walk.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// True when discovery and matching are disabled (<c>--no-rules</c>). When false,
    /// providers still synthesize sticky RULES.md; <see cref="DisableSticky"/> turns
    /// those off independently.
    /// </summary>
    public bool Disabled { get; init; }

    /// <summary>Turn off the synthesized RULES.md always-apply rules while keeping file rules.</summary>
    public bool DisableSticky { get; init; }

    /// <summary>Per-turn TTSR retry cap (plan §4.3).</summary>
    public int MaxStreamRetries { get; init; } = 3;

    /// <summary>Delay before a TTSR retry, in milliseconds (plan §4.3, default 0).</summary>
    public int RetryDelayMs { get; init; }
}
