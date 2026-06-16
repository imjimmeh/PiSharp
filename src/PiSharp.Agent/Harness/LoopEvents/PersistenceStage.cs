using PiSharp.Agent.Core.Events;

namespace PiSharp.Agent.Harness.LoopEvents;

internal sealed class PersistenceStage : ILoopEventStage
{
    public async Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
    {
        switch (context.CoreEvent)
        {
            case AgentEvent.MessageEnd { Message: var message }:
                await context.QueueWriteOrAppendAsync(message);
                break;
            case AgentEvent.TurnEnd:
            case AgentEvent.AgentEnd:
                await context.FlushWritesAsync(cancellationToken);
                break;
        }
    }
}
