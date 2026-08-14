using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.ContinualHarness.Contracts;

namespace PiSharp.ContinualHarness;

/// <summary>
/// The optional model-facing <c>refine</c> tool. Routes through the identical service path as the
/// <c>/refine</c> slash command with <c>Author = "model"</c> and evidence carried from what the model
/// observed in-turn. Rollback/list/show/diff are not model tools — they are user surfaces only.
/// </summary>
public sealed class RefineTool
{
    private readonly HarnessRefinementService _service;
    private readonly IHarnessSettings _settings;
    private readonly Func<bool> _gate;

    public const string ToolName = "refine";

    public RefineTool(HarnessRefinementService service, IHarnessSettings settings, Func<bool>? gate = null)
    {
        _service = service;
        _settings = settings;
        _gate = gate ?? (() => true);
    }

    public static JsonElement BuildSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "description": "Which harness state to refine: prompt|memory|skill|subagent." },
                "action": { "type": "string", "description": "One of create|update|delete." },
                "name": { "type": "string", "description": "Entry name (slug). Memory records use refine/<name> internally." },
                "content": { "type": "object", "description": "Content for create/update. Prompt/subagent: {\"markdown\":\"...\"}; skill: {\"description\":\"...\",\"content\":\"...\"}." },
                "scope": { "type": "string", "description": "local (default) or global." },
                "evidence": { "type": "array", "items": { "type": "string" }, "description": "Citations of what you observed that motivated the change." },
                "force": { "type": "boolean", "description": "Required when true to acknowledge a detected conflict." },
                "reason": { "type": "string", "description": "Why this refinement was made." }
              },
              "required": ["kind", "action", "name"],
              "additionalProperties": false
            }
            """);
        return doc.RootElement.Clone();
    }

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken ct = default,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        if (!_gate())
            return new AgentToolResult<object?>([new TextContent("refine is disabled.")], Details: null);

        try
        {
            string kindToken = GetString(parameters, "kind") ?? throw new HarnessRejectedException("refine requires 'kind'.");
            string actionToken = GetString(parameters, "action") ?? throw new HarnessRejectedException("refine requires 'action'.");
            string name = GetString(parameters, "name") ?? throw new HarnessRejectedException("refine requires 'name'.");
            string scopeToken = GetString(parameters, "scope") ?? "local";
            var force = parameters.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;

            if (actionToken.Equals("rollback", StringComparison.OrdinalIgnoreCase))
                throw new HarnessRejectedException("Rollback is a user-only surface; /refine rollback is not a model tool.");

            var kind = ParseKind(kindToken);
            var action = ParseAction(actionToken);
            var scope = scopeToken.Equals("global", StringComparison.OrdinalIgnoreCase)
                ? HarnessRefinementScope.Global
                : HarnessRefinementScope.Local;

            JsonElement? content = null;
            if (parameters.TryGetProperty("content", out var contentEl) && contentEl.ValueKind != JsonValueKind.Null)
                content = contentEl;

            var evidence = new List<RefinementEvidence>();
            if (parameters.TryGetProperty("evidence", out var evidenceEl) && evidenceEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in evidenceEl.EnumerateArray())
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        evidence.Add(new RefinementEvidence("model", EntryId: null, HarnessRefinementFormatter.BoundExcerpt(text)));
                }
            }

            var reason = GetString(parameters, "reason");

            var record = await _service.ApplyAsync(
                kind, action, name, content, scope,
                author: "model", evidence, force, reason, ct);

            return new AgentToolResult<object?>(
                [new TextContent($"Refined {kind.ToString().ToLowerInvariant()}/{name} ({action.ToString().ToLowerInvariant()}, v{record.Version}, #{record.RefinementId}).")],
                Details: record);
        }
        catch (HarnessConflictException conflict)
        {
            return new AgentToolResult<object?>(
                [new TextContent($"Conflict refused: {conflict.Message}\n---\n{conflict.Diff}")],
                Details: null);
        }
        catch (HarnessRejectedException rejected)
        {
            return new AgentToolResult<object?>(
                [new TextContent($"Refinement rejected: {rejected.Message}")],
                Details: null);
        }
    }

    private static string? GetString(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HarnessRefinementKind ParseKind(string token)
        => token.ToLowerInvariant() switch
        {
            "prompt" => HarnessRefinementKind.Prompt,
            "memory" => HarnessRefinementKind.Memory,
            "skill" => HarnessRefinementKind.Skill,
            "subagent" or "agent" => HarnessRefinementKind.Subagent,
            _ => throw new HarnessRejectedException($"Unknown refinement kind '{token}'."),
        };

    private static HarnessRefinementAction ParseAction(string token)
        => token.ToLowerInvariant() switch
        {
            "create" => HarnessRefinementAction.Create,
            "update" => HarnessRefinementAction.Update,
            "delete" => HarnessRefinementAction.Delete,
            _ => throw new HarnessRejectedException($"Unknown action '{token}'."),
        };
}
