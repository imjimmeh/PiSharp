using Microsoft.Extensions.Logging;
using PiSharp.Agent.Core.Events;

namespace PiSharp.Agent.Harness.LoopEvents;

internal sealed class ListenerNotificationStage : ILoopEventStage
{
    public async Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
    {
        if (context.Kind is HarnessEventKind.BeforeToolMiddleware or HarnessEventKind.AfterToolMiddleware)
        {
            return;
        }

        if (context.IsThinkingLevelOwnEvent)
        {
            context.Logger.LogDebug(
                "Harness listener notification starting harnessId={HarnessId} event={EventName} listenerCount={ListenerCount}",
                context.HarnessId,
                context.EventName,
                context.ListenerCount);
        }

        var notifications = context.Listeners.Select((listener, index) => NotifyListenerAsync(context, listener, index, cancellationToken));
        await Task.WhenAll(notifications);

        if (context.IsThinkingLevelOwnEvent)
        {
            context.Logger.LogDebug(
                "Harness listener notification finished harnessId={HarnessId} event={EventName} listenerCount={ListenerCount}",
                context.HarnessId,
                context.EventName,
                context.ListenerCount);
        }
    }

    private static async Task NotifyListenerAsync(
        HarnessEventContext context,
        Func<AgentHarnessEvent, CancellationToken, Task> listener,
        int index,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.IsThinkingLevelOwnEvent)
            {
                context.Logger.LogDebug(
                    "Harness listener notification invoking harnessId={HarnessId} event={EventName} listenerIndex={ListenerIndex}",
                    context.HarnessId,
                    context.EventName,
                    index);
            }

            await listener(context.Event, cancellationToken);

            if (context.IsThinkingLevelOwnEvent)
            {
                context.Logger.LogDebug(
                    "Harness listener notification completed harnessId={HarnessId} event={EventName} listenerIndex={ListenerIndex}",
                    context.HarnessId,
                    context.EventName,
                    index);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Listener failures must not break persistence or mode execution.
        }
    }
}
