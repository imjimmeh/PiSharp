using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;

namespace PiSharp.Agent.Harness.LoopEvents;

internal sealed class ToolMiddlewareStage : ILoopEventStage
{
    public async Task ExecuteAsync(HarnessEventContext context, CancellationToken cancellationToken)
    {
        if (context.Kind is not (HarnessEventKind.BeforeToolMiddleware or HarnessEventKind.AfterToolMiddleware) || context.Middleware.Count == 0)
        {
            return;
        }

        var middlewareContext = new ExtensionMiddlewareContext(
            context.ExtensionEvent,
            context.BeforeToolCall,
            context.AfterToolCall);

        foreach (var middleware in context.Middleware)
        {
            await middleware.Value(middlewareContext, (_, _) => Task.CompletedTask, cancellationToken);
        }

        if (context.Kind == HarnessEventKind.BeforeToolMiddleware && middlewareContext.Blocked)
        {
            context.BeforeToolCallResult = new BeforeToolCallResult(true, middlewareContext.BlockReason);
        }
        else if (context.Kind == HarnessEventKind.AfterToolMiddleware && middlewareContext.Modified && context.AfterToolCall is not null)
        {
            context.AfterToolCallResult = new AfterToolCallResult(
                middlewareContext.ModifiedContent ?? context.AfterToolCall.Result.Content,
                middlewareContext.ModifiedDetails ?? context.AfterToolCall.Result.Details,
                middlewareContext.IsError ?? context.AfterToolCall.IsError);
        }
    }
}
