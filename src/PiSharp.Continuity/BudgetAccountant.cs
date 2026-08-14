using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;

namespace PiSharp.Continuity;

/// <summary>
/// Listens to in-process <c>message_end</c> events and routes assistant-message
/// token usage into the goal. Per plan §4.2: tokens are accounted when (a) a
/// goal exists in <c>active</c> status (handled by <see cref="GoalService"/>) and
/// (b) the message timestamp is at/after <c>StartedAt</c>. <c>Usage</c> is null
/// for non-assistant messages and zero-filled otherwise; only
/// <c>Usage.TotalTokens</c> is consumed.
/// </summary>
public sealed class BudgetAccountant
{
    private readonly GoalService _goals;

    public BudgetAccountant(GoalService goals, IContinuityEvents events, ContinuityClock clock)
    {
        _goals = goals;
    }

    public async Task OnMessageEndAsync(AgentEvent.MessageEnd messageEnd, CancellationToken ct)
    {
        if (messageEnd.Message is not AssistantMessage assistant
            || assistant.Usage is not { } usage
            || usage.TotalTokens <= 0)
        {
            return;
        }

        var (_, hitBudget) = await _goals.AccountTokensAsync(usage.TotalTokens, assistant.Timestamp, ct);
        if (!hitBudget)
        {
            // Emit the budget line without forcing a redundant goal write.
            await _goals.EmitBudgetOnlyAsync(null, "goal", ct);
        }
    }
}
