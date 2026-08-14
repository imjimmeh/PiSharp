namespace PiSharp.Extensions;

/// <summary>
/// Richer skill registration shape (GAP-56). Extends the flat
/// <see cref="ExtensionSkillRegistration"/> shape with pipeline metadata:
/// globs/alwaysApply/hide (prompt composition), source/sourcePriority
/// (first-wins dedup ordering across skill providers), and an optional
/// per-skill runner hook that executes instead of raw markdown injection.
/// </summary>
public record ExtensionSkillDefinition(
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false,
    ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject,
    IReadOnlyList<string>? Globs = null,
    bool AlwaysApply = false,
    bool Hide = false,
    string? Source = null,          // provider source id: "extension:<id>", "managed", "omp", "claude", ...
    int SourcePriority = 0,          // first-wins ordering; higher = wins on name collision
    ExtensionSkillRunner? Runner = null);

/// <summary>
/// Backward-compatible flat skill registration; a thin subtype of
/// <see cref="ExtensionSkillDefinition"/> so existing call sites and tests
/// keep building against the flat shape.
/// </summary>
public sealed record ExtensionSkillRegistration(
    string Name,
    string Description,
    string Content,
    string FilePath,
    bool DisableModelInvocation = false,
    ExtensionOverridePolicy Override = ExtensionOverridePolicy.Reject)
    : ExtensionSkillDefinition(Name, Description, Content, FilePath, DisableModelInvocation, Override);

/// <summary>Context passed to a per-skill <see cref="ExtensionSkillRunner"/> when the harness invokes the skill.</summary>
public sealed record ExtensionSkillRunContext(
    string Name,
    string Body,
    string? AdditionalInstructions,
    IReadOnlyList<string> Args);

/// <summary>Result of a per-skill runner invocation. <see cref="Content"/> replaces the markdown injection.</summary>
public sealed record ExtensionSkillRunResult(string? Content, object? Details = null);

/// <summary>
/// Per-skill runner hook (GAP-56 "middle path" between pure markdown and
/// impure python packages). When a skill declares a runner, the harness calls
/// it instead of injecting the raw markdown body.
/// </summary>
public delegate Task<ExtensionSkillRunResult> ExtensionSkillRunner(ExtensionSkillRunContext context, CancellationToken ct);

/// <summary>
/// Generic skill provider (P11 supplies concrete foreign-format providers:
/// omp/claude/codex/github). Discovered skills merge with first-wins dedup by
/// name — higher <see cref="ExtensionSkillDefinition.SourcePriority"/> wins.
/// </summary>
public interface ISkillProvider
{
    string Name { get; }
    int Priority { get; }                                   // first-wins dedup order
    Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverAsync(CancellationToken ct = default);
}

/// <summary>Descriptor of a skill stored in the daemon-resident managed-skill store.</summary>
public sealed record ManagedSkillDescriptor(
    string Name,
    string Description,
    string Content,
    bool DisableModelInvocation = false,
    string? Source = null,
    int SourcePriority = 0);

public sealed record ManagedSkillCreateRequest(string Name, string Description, string Content, bool DisableModelInvocation = false);
public sealed record ManagedSkillUpdateRequest(string? Description = null, string? Content = null, bool? DisableModelInvocation = null);

/// <summary>
/// Isolated managed-skill store (GAP-56). Managed skills live daemon-side at
/// <c>~/.pi/PiSharp/managed-skills</c>, are registered with
/// <c>Source="managed"</c>, and can be promoted from existing skills
/// (learn-to-skill, consumed by P08/P09).
/// </summary>
public interface IExtensionManagedSkillApi
{
    Task<ManagedSkillDescriptor> CreateAsync(ManagedSkillCreateRequest request, CancellationToken ct = default);
    Task<ManagedSkillDescriptor> UpdateAsync(string name, ManagedSkillUpdateRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<ManagedSkillDescriptor>> ListAsync(CancellationToken ct = default);
    Task<ManagedSkillDescriptor> PromoteAsync(string sourceReference, CancellationToken ct = default); // learn-to-skill promotion
}
