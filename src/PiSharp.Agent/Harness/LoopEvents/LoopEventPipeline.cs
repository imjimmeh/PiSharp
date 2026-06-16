namespace PiSharp.Agent.Harness.LoopEvents;

// Central harness event delivery policy:
// 1. persist core loop writes before side effects,
// 2. apply phase transitions before observers,
// 3. run tool middleware only for middleware event kinds,
// 4. dispatch extension-visible core/own events,
// 5. notify harness listeners last.
// Stages opt in by HarnessEventKind so core loop events, own events, before-agent-start,
// and tool middleware share one policy path without changing existing observer behavior.
internal sealed class LoopEventPipeline(IReadOnlyList<ILoopEventStage> stages)
{
    private readonly IReadOnlyList<ILoopEventStage> _stages = stages;

    public async Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
    {
        foreach (var stage in _stages)
        {
            await stage.ExecuteAsync(context, cancellationToken);
        }
    }
}
