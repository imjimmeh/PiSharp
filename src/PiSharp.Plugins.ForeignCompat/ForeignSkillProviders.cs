using PiSharp.Extensions;

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// One <see cref="ISkillProvider"/> per priority tier, not per format (P11 plan §4.2):
/// each provider hosts its tool's <c>SKILL.md</c> roots, merges into a single
/// <c>DiscoverAsync</c> list, and gates on its P02 toggle (plan §4.3) — a disabled
/// source returns an empty list.
/// </summary>
public abstract class ForeignSkillProviderBase : ISkillProvider
{
    private readonly ForeignCompatOptions _options;
    private readonly Func<ForeignCompatOptions, bool> _isEnabled;
    private readonly string _toolDir;

    protected ForeignSkillProviderBase(ForeignCompatOptions options, string name, int priority, string toolDir, Func<ForeignCompatOptions, bool> isEnabled)
    {
        _options = options;
        Name = name;
        Priority = priority;
        _toolDir = toolDir;
        _isEnabled = isEnabled;
    }

    public string Name { get; }

    public int Priority { get; }

    public Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverAsync(CancellationToken ct = default)
    {
        if (!_isEnabled(_options)) return Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>([]);
        var roots = ForeignPaths.DiscoverSkillDirs(_options.Roots, _toolDir);
        return ForeignSkillDiscovery.DiscoverFromRootsAsync(roots, Name, Priority, _options, ct);
    }
}

/// <summary>Claude (<c>.claude/skills/**/SKILL.md</c>), tier 80.</summary>
public sealed class ClaudeSkillProvider : ForeignSkillProviderBase
{
    public ClaudeSkillProvider(ForeignCompatOptions options)
        : base(options, "claude", ForeignCompatTiers.Claude, ".claude", o => o.EnableClaudeUser) { }
}

/// <summary>Codex (<c>.codex/skills/**/SKILL.md</c>), tier 70.</summary>
public sealed class CodexSkillProvider : ForeignSkillProviderBase
{
    public CodexSkillProvider(ForeignCompatOptions options)
        : base(options, "codex", ForeignCompatTiers.Codex, ".codex", o => o.EnableCodexUser) { }
}

/// <summary>Gemini (<c>.gemini/skills/**/SKILL.md</c>), tier 60.</summary>
public sealed class GeminiSkillProvider : ForeignSkillProviderBase
{
    public GeminiSkillProvider(ForeignCompatOptions options)
        : base(options, "gemini", ForeignCompatTiers.Gemini, ".gemini", o => o.EnableGeminiUser) { }
}

/// <summary>OpenCode (<c>.opencode/skills/**/SKILL.md</c>), tier 55.</summary>
public sealed class OpenCodeSkillProvider : ForeignSkillProviderBase
{
    public OpenCodeSkillProvider(ForeignCompatOptions options)
        : base(options, "opencode", ForeignCompatTiers.OpenCode, ".opencode", o => o.EnableOpenCode) { }
}

/// <summary>Cursor (<c>.cursor/skills/**/SKILL.md</c>), tier 50.</summary>
public sealed class CursorSkillProvider : ForeignSkillProviderBase
{
    public CursorSkillProvider(ForeignCompatOptions options)
        : base(options, "cursor", ForeignCompatTiers.Cursor, ".cursor", o => o.EnableCursorUser) { }
}

/// <summary>Cline (<c>.cline/skills/**/SKILL.md</c>), tier 50.</summary>
public sealed class ClineSkillProvider : ForeignSkillProviderBase
{
    public ClineSkillProvider(ForeignCompatOptions options)
        : base(options, "cline", ForeignCompatTiers.Cline, ".cline", o => o.EnableClineUser) { }
}

/// <summary>GitHub (<c>.github/skills/**/SKILL.md</c>), tier 30.</summary>
public sealed class GithubSkillProvider : ForeignSkillProviderBase
{
    public GithubSkillProvider(ForeignCompatOptions options)
        : base(options, "github", ForeignCompatTiers.Github, ".github", o => o.EnableGithubUser) { }
}
