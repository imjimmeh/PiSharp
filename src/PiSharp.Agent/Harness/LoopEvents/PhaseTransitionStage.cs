using PiSharp.Agent.Core.Events;

namespace PiSharp.Agent.Harness.LoopEvents;

internal sealed class PhaseTransitionStage : ILoopEventStage
{
    public Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
    {
        if (context.CoreEvent is AgentEvent.AgentEnd)
        {
            context.SetPhase(AgentHarnessPhase.Idle);
        }

        return Task.CompletedTask;
    }
}
