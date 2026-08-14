namespace PiSharp.Extensions;

public enum RuleApplyMode
{
    /// <summary>Force-applied on every stream request.</summary>
    Always,

    /// <summary>Matched against the visible stream text; fires on the token boundary where the pattern first matches.</summary>
    StreamTrigger
}

/// <summary>
/// A single rule. <see cref="Path"/> is the source file (diagnostics/events);
/// <see cref="Priority"/> is first-wins — higher wins on <see cref="Name"/> collision.
/// </summary>
public sealed record Rule(
    string Name,
    string Content,
    string? Path = null,
    int Priority = 0,
    RuleApplyMode ApplyMode = RuleApplyMode.StreamTrigger,
    string? TriggerPattern = null)   // required when ApplyMode == StreamTrigger; regex (case-sensitive, timeout-guarded)
{
    /// <summary>True when the rule participates in mid-stream matching.</summary>
    public bool IsStreamTrigger => ApplyMode == RuleApplyMode.StreamTrigger;
}

/// <summary>
/// Rule discovery provider. P11 implements one per foreign format.
/// </summary>
public interface IRuleProvider
{
    /// <summary>Provider source id, e.g. "rules-dir", "rules-md", "cursor", "cline".</summary>
    string Name { get; }

    /// <summary>Discovery order; higher wins on rule <see cref="Rule.Name"/> collision.</summary>
    int Priority { get; }

    Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IExtensionApi.Rules"/> — typed registration + query surface for native and TS extensions.
/// </summary>
public interface IExtensionRuleApi
{
    /// <summary>
    /// Registers a rule provider. A duplicate <see cref="IRuleProvider.Name"/> replaces the
    /// previous registration (last-wins) with a warning.
    /// </summary>
    IDisposable RegisterProvider(IRuleProvider provider);

    /// <summary>All rules across registered providers — post-dedup, priority-ordered.</summary>
    Task<IReadOnlyList<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Names of all registered rule providers.</summary>
    IReadOnlyList<string> GetProviderNames();
}
