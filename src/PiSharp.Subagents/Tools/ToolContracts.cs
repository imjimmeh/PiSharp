using System.ComponentModel;
using System.Text.Json;

namespace PiSharp.Subagents.Tools;

/// <summary>Input contract of the model-callable <c>task</c> spawn tool.</summary>
public sealed record TaskToolInput(
    [property: Description("Name of the agent definition to spawn.")] string Agent,
    [property: Description("Instruction / task for the subagent.")] string Task,
    [property: Description("(optional) Output schema overriding the agent's frontmatter output schema.")] JsonElement? OutputSchema = null,
    [property: Description("(optional) Comma-separated tool restriction for this single spawn.")] string[]? Tools = null);

/// <summary>Input contract of the <c>yield</c> result tool: the raw object to return as the
/// child's structured result.</summary>
public sealed record YieldToolInput(JsonElement Data);
