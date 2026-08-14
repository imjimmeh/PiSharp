using System.Text.Json;
using PiSharp.Agent.Core.Tools;
using PiSharp.Abstractions.Messages;
using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// The model's only continuity surface (<c>update_goal</c>): lets the model
/// update the goal's free-text progress or transition it to
/// <c>complete</c>/<c>active</c>/<c>paused</c>.
/// </summary>
public sealed class UpdateGoalTool
{
    private readonly GoalService _goals;

    public UpdateGoalTool(GoalService goals) => _goals = goals;

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken ct,
        AgentToolUpdateCallback<object?>? onUpdate = null)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            return TextError("update_goal requires an object argument.");

        string? progress = null;
        string? status = null;
        if (parameters.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.String)
            progress = p.GetString();
        if (parameters.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
            status = s.GetString();

        try
        {
            if (!string.IsNullOrWhiteSpace(progress))
                await _goals.SetProgressAsync(progress!, ct);

            if (!string.IsNullOrWhiteSpace(status))
            {
                switch (status!.ToLowerInvariant())
                {
                    case "active": await _goals.ResumeAsync(ct); break;
                    case "paused": await _goals.PauseAsync(ct); break;
                    case "complete": await _goals.CompleteAsync(ct); break;
                    default: return TextError($"update_goal status '{status}' is invalid (expected active|paused|complete).");
                }
            }

            var goal = await _goals.GetGoalAsync(ct);
            var detail = goal.Goal is { } g
                ? $"goal={g.Status.ToString().ToLowerInvariant()} progress='{g.Progress}' tokens={g.TokensUsed}" + (g.MaxTokens is { } m ? $"/{m}" : "/unlimited")
                : "no goal set";
            return new AgentToolResult<object?>([new TextContent(detail)], null);
        }
        catch (Exception ex)
        {
            return TextError($"update_goal failed: {ex.Message}");
        }
    }

    private static AgentToolResult<object?> TextError(string message)
        => new([new TextContent(message)], null);
}
