using System.Text.Json;
using PiSharp.Abstractions.Options;

namespace PiSharp.Subagents.AgentDefinitions;

/// <summary>Source tier of an agent definition; discovery is first-wins in ascending order.</summary>
public enum AgentSourceKind
{
    Project = 0,
    User = 1,
    Extension = 2,
    Bundled = 3,
}

/// <summary>
/// Immutable parsed agent definition (markdown file + YAML frontmatter). The markdown body after the
/// frontmatter is the system prompt unless <see cref="SystemPrompt"/> is supplied in frontmatter.
/// </summary>
public sealed record AgentDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string SystemPrompt { get; init; }

    /// <summary>Explicit active-tool allowlist; empty = inherit the parent's active set (always
    /// auto-adds the <c>yield</c> result tool).</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Agent names this agent may spawn via <c>task</c> (default <c>["task"]</c>); empty
    /// disables spawning entirely.</summary>
    public IReadOnlyList<string> Spawns { get; init; } = ["task"];

    public string? Model { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }

    /// <summary>JSON Schema document the agent's <c>yield</c> output must conform to.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>Additive skills to enable in-scope for this agent (over inherited selection).</summary>
    public IReadOnlyList<string> AutoloadSkills { get; init; } = [];

    /// <summary>Explicit skill restrict set that replaces the inherited selection; conflicts with
    /// <see cref="AutoloadSkills"/> are an author error.</summary>
    public IReadOnlyList<string>? RestrictSkills { get; init; }

    /// <summary>Inject a "summarize large reads" guideline into the system prompt.</summary>
    public bool ReadSummarize { get; init; }

    /// <summary>Omit from the <c>/agents</c> listing (still spawnable by explicit name).</summary>
    public bool Hide { get; init; }

    /// <summary>Filesystem path the definition was loaded from (embedded resources use a virtual path).</summary>
    public required string SourcePath { get; init; }

    public required AgentSourceKind Source { get; init; }
}
