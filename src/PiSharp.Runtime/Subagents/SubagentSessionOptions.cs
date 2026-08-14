using System.Text.Json;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Runtime.Subagents;

/// <summary>
/// Options for creating a subagent child session. All optional members inherit the parent's
/// configuration when null; <see cref="SubagentSessionService.CreateAsync"/> is the only consumer.
/// </summary>
public sealed record SubagentSessionOptions(
    ModelDescriptor? Model = null,
    ThinkingLevel? ThinkingLevel = null,
    string? SessionName = null,
    string? ParentSessionPath = null,
    /// <summary>Extra tools to register into the child harness (e.g. the plugin's <c>yield</c> tool).</summary>
    IReadOnlyList<IAgentTool>? Tools = null,
    /// <summary>Restrict the child's active tool set to these names (null = inherit parent's set).</summary>
    IReadOnlyList<string>? ActiveToolNames = null,
    /// <summary>Skill policy for the child: explicit selected set (null = inherit parent's selection).</summary>
    IReadOnlyList<string>? SelectedSkillNames = null,
    /// <summary>Effective output schema for the child's structured <c>yield</c>, stored on the handle.</summary>
    JsonElement? OutputSchema = null,
    /// <summary>Isolation working directory the child should run in (C3; reserved, not yet wired).</summary>
    string? Cwd = null,
    /// <summary>Spawn guardrail policy; defaults to <see cref="SubagentSpawnPolicy.Default"/> when null.</summary>
    SubagentSpawnPolicy? SpawnPolicy = null,
    /// <summary>Agent-definition name of the child being created (null for anonymous programmatic spawns).</summary>
    string? AgentName = null,
    /// <summary>Agent-definition name of the requesting parent (drives the self-recursion guard).</summary>
    string? ParentAgentName = null,
    /// <summary>Session id of the requesting parent session, if known (event payloads).</summary>
    string? ParentSessionId = null,
    /// <summary>Recursion depth of the requesting parent; the child runs at <c>Depth + 1</c>.</summary>
    int Depth = 0);
