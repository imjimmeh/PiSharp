using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Tools;

using PiSharp.Runtime;
using PiSharp.Runtime.Subagents;
using PiSharp.Subagents.AgentDefinitions;
using PiSharp.Subagents.Discovery;
using PiSharp.Subagents.Tools;

namespace PiSharp.Subagents.Spawning;

public sealed record SpawnOutcome(
    bool Success,
    JsonElement? StructuredResult = null,
    string? BlockReason = null,
    string? Error = null)
{
    public static SpawnOutcome Ok(JsonElement? structuredResult) => new(true, structuredResult);
    public static SpawnOutcome Blocked(string reason) => new(false, null, reason);
    public static SpawnOutcome Failed(string error) => new(false, null, null, error);
}

/// <summary>
/// Orchestrates discovery → policy → <see cref="SubagentSessionService"/> → typed result for the
/// <c>task</c> spawn tool. The runtime service remains the bottom-line enforcement point; this
/// coordinator derives the per-spawn <see cref="SubagentSessionOptions"/> and emits lifecycle events.
/// </summary>
public sealed class SubagentSpawnCoordinator
{
    private readonly AgentDefinitionRegistry _registry;
    private readonly SubagentSettings _settings;
    private readonly SubagentSessionService? _service;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _getParentSelectedSkills;
    private readonly Func<string, object?, CancellationToken, Task>? _emitEvent;
    private readonly string? _parentSessionId;
    private readonly string? _parentAgentName;
    private readonly int _depth;
    private readonly IReadOnlyList<string> _parentSpawns;

    public SubagentSpawnCoordinator(
        AgentDefinitionRegistry registry,
        SubagentSettings settings,
        SubagentSessionService? service = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? getParentSelectedSkills = null,
        Func<string, object?, CancellationToken, Task>? emitEvent = null,
        string? parentSessionId = null,
        string? parentAgentName = null,
        int depth = 0,
        IReadOnlyList<string>? parentSpawns = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _service = service;
        _getParentSelectedSkills = getParentSelectedSkills;
        _emitEvent = emitEvent;
        _parentSessionId = parentSessionId;
        _parentAgentName = parentAgentName;
        _depth = depth;
        _parentSpawns = parentSpawns ?? [];
    }

    /// <summary>The agent definitions backing this coordinator's spawns.</summary>
    public AgentDefinitionRegistry Registry => _registry;
    /// <summary>
    /// Plan-mode hook (consumed by P14): returns the spawn/tool policy a definition should run under
    /// when plan mode is active — <c>spawns</c> is cleared (no spawning) and the tool set is narrowed
    /// to read-oriented tools plus <c>yield</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ApplyPlanMode(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["spawns"] = [],
            ["tools"] = definition.Tools.Count > 0
                ? definition.Tools.Intersect(PlanModeReadTools, StringComparer.Ordinal).Append(SubagentSessionService.YieldToolName).ToArray()
                : [SubagentSessionService.YieldToolName],
        };
    }

    private static readonly string[] PlanModeReadTools =
    [
        "read",
        "grep",
        "glob",
        "bash",
        "web_search",
        "eval",
    ];

    /// <summary>Spawns the named agent, derives options (tools, skills, model, schema), runs the
    /// child, and returns the typed structured result (or a block/failure outcome).</summary>
    public async Task<SpawnOutcome> SpawnAsync(TaskToolInput input, string toolCallId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Agent))
            return SpawnOutcome.Blocked("unknown-agent");
        if (string.IsNullOrWhiteSpace(input.Task))
            return SpawnOutcome.Blocked("missing-task");

        var definition = _registry.TryGet(input.Agent);
        if (definition is null)
            return SpawnOutcome.Blocked("unknown-agent");
        if (_registry.IsDisabled(input.Agent))
            return SpawnOutcome.Blocked("disabled");

        // Local pre-checks mirror the service policy so the model gets a readable tool result;
        // the service still re-enforces everything at CreateAsync.
        if (_parentAgentName is not null
            && StringComparer.Ordinal.Equals(_parentAgentName, input.Agent)
            && !definition.Spawns.Contains(input.Agent))
            return SpawnOutcome.Blocked("self-recursion");
        if (_depth + 1 > _settings.MaxRecursionDepth)
            return SpawnOutcome.Blocked("max-recursion-depth");
        if (_parentSpawns.Count > 0 && !_parentSpawns.Contains(input.Agent))
            return SpawnOutcome.Blocked("not-allowed");

        var service = _service ?? SubagentRuntimeAccess.Current;
        if (service is null)
            return SpawnOutcome.Failed("Subagent session service is not available.");

        // Output-schema precedence: per-spawn override → frontmatter → none.
        var effectiveSchema = input.OutputSchema ?? definition.OutputSchema;

        // Tool policy: per-spawn Tools wins over the definition's allowlist; `yield` is always added.
        var requestedTools = input.Tools is { Length: > 0 } ? input.Tools : definition.Tools;
        var activeToolNames = requestedTools.Count > 0
            ? requestedTools.Concat([SubagentSessionService.YieldToolName]).Distinct(StringComparer.Ordinal).ToArray()
            : null; // inherit all (yield is still callable: registered tools are all active)

        var selectedSkills = await ResolveSelectedSkillsAsync(definition, cancellationToken);

        // Children may spawn when their definition allows it and their tool set exposes `task`.
        var canSpawn = definition.Spawns.Count > 0;
        var childCoordinator = canSpawn
            ? new SubagentSpawnCoordinator(
                _registry,
                _settings,
                service,
                _getParentSelectedSkills,
                _emitEvent,
                parentSessionId: null,
                parentAgentName: definition.Name,
                depth: _depth + 1,
                parentSpawns: definition.Spawns)
            : null;

        var childTools = new List<IAgentTool> { new YieldTool(effectiveSchema) };
        if (childCoordinator is not null
            && (definition.Tools.Count == 0 || definition.Tools.Contains(SubagentSessionService.SpawnToolName, StringComparer.Ordinal)))
            childTools.Add(new TaskTool(childCoordinator));

        var options = new SubagentSessionOptions(
            Model: ResolveModel(definition),
            ThinkingLevel: definition.ThinkingLevel,
            SessionName: definition.Name,
            Tools: childTools,
            ActiveToolNames: activeToolNames,
            SelectedSkillNames: selectedSkills,
            OutputSchema: effectiveSchema,
            SpawnPolicy: new SubagentSpawnPolicy(
                MaxRecursionDepth: _settings.MaxRecursionDepth,
                DisabledAgents: _settings.DisabledAgents,
                ParentSpawns: _parentSpawns.Count > 0
                    ? new HashSet<string>(_parentSpawns, StringComparer.Ordinal)
                    : null),
            AgentName: definition.Name,
            ParentAgentName: _parentAgentName,
            ParentSessionId: _parentSessionId,
            Depth: _depth);

        SubagentSessionHandle? handle = null;
        try
        {
            handle = await service.CreateAsync(options, cancellationToken);
            await EmitCreatedAsync(definition.Name, handle.SessionId, cancellationToken);
            await EmitStartedAsync(handle.SessionId, definition.Name, toolCallId, cancellationToken);

            var result = await service.PromptAsync(handle.SessionId, input.Task, cancellationToken);

            await EmitCompletedAsync(handle.SessionId, definition.Name, result.StructuredResult, "completed", cancellationToken);
            return SpawnOutcome.Ok(result.StructuredResult);
        }
        catch (SubagentSpawnBlockedException blocked)
        {
            await EmitBlockedAsync(blocked.Agent, blocked.Reason, _depth + 1, cancellationToken);
            return SpawnOutcome.Blocked(blocked.Reason);
        }
        finally
        {
            if (handle is not null)
            {
                try { await service.DisposeAsync(handle.SessionId, CancellationToken.None); }
                catch { }
            }
        }
    }

    /// <summary>Skill policy: explicit <c>skills</c> replaces the inherited selection; otherwise the
    /// parent's selection plus additive <c>autoloadSkills</c> (unknown names silently ignored by the
    /// harness). Global <c>subagents.autoloadSkills</c> applies to every child.</summary>
    private async Task<IReadOnlyList<string>?> ResolveSelectedSkillsAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.RestrictSkills is not null)
            return definition.RestrictSkills.Count > 0 ? definition.RestrictSkills : null;

        var baseSkills = new List<string>(_settings.AutoloadSkills);
        if (_getParentSelectedSkills is not null)
        {
            try { baseSkills.AddRange(await _getParentSelectedSkills(cancellationToken)); }
            catch { /* parent skill query is best-effort */ }
        }
        baseSkills.AddRange(definition.AutoloadSkills);

        return baseSkills.Count > 0 ? baseSkills.Distinct(StringComparer.Ordinal).ToArray() : null;
    }

    private ModelDescriptor? ResolveModel(AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Model))
            return null;
        try
        {
            var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(
                Provider: null,
                Model: definition.Model,
                Thinking: definition.ThinkingLevel));
            return selection.Model;
        }
        catch
        {
            // Unknown model id falls back to the parent model (omp parity: parent model default).
            return null;
        }
    }

    private Task EmitCreatedAsync(string agent, string sessionId, CancellationToken cancellationToken)
    {
        var payload = new SubagentCreatedEvent(agent, sessionId, _parentSessionId, _depth + 1);
        var observed = new SubagentsObservedEvent(sessionId, Type: agent, Status: "created");
        return EmitBothAsync(SubagentEventNames.Created, SubagentEventNames.ColonCreated, payload, observed, cancellationToken);
    }

    private Task EmitStartedAsync(string sessionId, string agent, string toolCallId, CancellationToken cancellationToken)
    {
        var payload = new SubagentStartedEvent(sessionId, agent, toolCallId);
        var observed = new SubagentsObservedEvent(sessionId, Type: agent, Status: "started");
        return EmitBothAsync(SubagentEventNames.Started, SubagentEventNames.ColonStarted, payload, observed, cancellationToken);
    }

    private Task EmitCompletedAsync(string sessionId, string agent, JsonElement? structuredResult, string status, CancellationToken cancellationToken)
    {
        var payload = new SubagentCompletedEvent(sessionId, agent, structuredResult, status);
        var observed = new SubagentsObservedEvent(sessionId, Type: agent, Status: status);
        return EmitBothAsync(SubagentEventNames.Completed, SubagentEventNames.ColonCompleted, payload, observed, cancellationToken);
    }

    private Task EmitBlockedAsync(string agent, string reason, int depth, CancellationToken cancellationToken)
    {
        var payload = new SubagentBlockedEvent(agent, reason, depth);
        var observed = new SubagentsObservedEvent(agent, Type: agent, Status: "blocked");
        return EmitBothAsync(SubagentEventNames.Blocked, SubagentEventNames.ColonBlocked, payload, observed, cancellationToken);
    }

    private async Task EmitBothAsync(
        string snakeCaseName,
        string colonName,
        object snakeCasePayload,
        object colonPayload,
        CancellationToken cancellationToken)
    {
        if (_emitEvent is null)
            return;
        try
        {
            await _emitEvent(snakeCaseName, snakeCasePayload, cancellationToken);
            await _emitEvent(colonName, colonPayload, cancellationToken);
        }
        catch
        {
            // Event emission must never break a spawn.
        }
    }
}
