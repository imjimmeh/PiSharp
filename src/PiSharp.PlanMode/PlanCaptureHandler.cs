using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;

namespace PiSharp.PlanMode;

/// <summary>
/// Subscribes <c>agent_end</c>; while <see cref="PlanModePhase.Planning"/>, persists the last
/// assistant message's text as a draft plan file and exposes it as the approval candidate
/// (<see cref="PlanModeService.LastPlanBody"/>). No-op in every other phase.
/// </summary>
public sealed class PlanCaptureHandler(PlanModeService service)
{
    public Task OnAgentEndAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (service.Phase != PlanModePhase.Planning) return Task.CompletedTask;
        if (evt.Payload is not AgentEvent.AgentEnd agentEnd) return Task.CompletedTask;

        var lastAssistant = agentEnd.Messages.OfType<AssistantMessage>().LastOrDefault();
        if (lastAssistant is null) return Task.CompletedTask;

        var text = ExtractText(lastAssistant);
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

        return service.CapturePlanAsync(text, cancellationToken);
    }

    /// <summary>Joins the assistant message's text content (tool-call-only messages yield empty).</summary>
    public static string ExtractText(AssistantMessage message)
        => string.Join('\n', message.Content.OfType<TextContent>().Select(content => content.Text)).Trim();
}
