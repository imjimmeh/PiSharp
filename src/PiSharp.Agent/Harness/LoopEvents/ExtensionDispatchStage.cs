using Microsoft.Extensions.Logging;

namespace PiSharp.Agent.Harness.LoopEvents;

internal sealed class ExtensionDispatchStage : ILoopEventStage
{
    public async Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
    {
        if (context.DispatchExtensionEventAsync is null || context.Kind is HarnessEventKind.BeforeToolMiddleware or HarnessEventKind.AfterToolMiddleware)
        {
            return;
        }

        if (context.IsThinkingLevelOwnEvent)
        {
            context.Logger.LogDebug(
                "Harness extension dispatch starting harnessId={HarnessId} event={EventName} handlerCount={HandlerCount}",
                context.HarnessId,
                context.EventName,
                context.ExtensionHandlerCount);
        }

        try
        {
            await context.DispatchExtensionEventAsync(context.ExtensionEvent, cancellationToken);
            if (context.IsThinkingLevelOwnEvent)
            {
                context.Logger.LogDebug(
                    "Harness extension dispatch completed harnessId={HarnessId} event={EventName} handlerCount={HandlerCount}",
                    context.HarnessId,
                    context.EventName,
                    context.ExtensionHandlerCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Extension handler failures must not break persistence or mode execution.
        }
    }
}
