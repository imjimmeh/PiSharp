namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// Foreign-source priority ladder (omp master analysis §5/§13; P11 plan §4.7).
/// Mirrored onto <see cref="PiSharp.Extensions.ExtensionSkillDefinition.SourcePriority"/>
/// for skills and <see cref="PiSharp.Extensions.Rule.Priority"/> for rules — both are
/// first-wins, so a single ordering concept drives the two halves.
/// Native <c>.pi</c> skills sit at 100 (untouched) and P04 managed skills at 5.
/// </summary>
public static class ForeignCompatTiers
{
    /// <summary>Claude (<c>.claude/skills</c>).</summary>
    public const int Claude = 80;

    /// <summary>Codex (<c>.codex/skills</c>).</summary>
    public const int Codex = 70;

    /// <summary>Gemini (<c>.gemini/skills</c>, <c>.gemini/rules</c>).</summary>
    public const int Gemini = 60;

    /// <summary>OpenCode (<c>.opencode/skills</c>).</summary>
    public const int OpenCode = 55;

    /// <summary>Cursor (<c>.cursor/skills</c>, <c>.cursorrules</c>, <c>.cursor/rules</c>).</summary>
    public const int Cursor = 50;

    /// <summary>Cline (<c>.cline/skills</c>, <c>.clinerules</c>).</summary>
    public const int Cline = 50;

    /// <summary>Copilot (<c>.github/copilot-instructions.md</c>).</summary>
    public const int Copilot = 40;

    /// <summary>GitHub (<c>.github/skills</c>, <c>.github/rules</c>).</summary>
    public const int Github = 30;

    /// <summary>Repo rules (<c>**/RULES.md</c>, <c>.pisharp/RULES.md</c>).</summary>
    public const int Repo = 20;
}
