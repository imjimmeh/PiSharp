namespace PiSharp.Agent.Harness.LoopEvents;

internal interface ILoopEventStage
{
    Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken);
}
