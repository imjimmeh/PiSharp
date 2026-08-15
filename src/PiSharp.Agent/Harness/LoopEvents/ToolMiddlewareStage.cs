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

        // Sticky block flag: once any middleware sets Blocked it sticks, so a later
        // middleware cannot un-block the decision and nothing downstream runs.
        var blocked = false;

        await context.Middleware[0].Value(middlewareContext, NextFor(0), cancellationToken);
        blocked = blocked || middlewareContext.Blocked;

        if (context.Kind == HarnessEventKind.BeforeToolMiddleware && blocked)
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


        // Composes middlewares so the next of middleware i invokes middleware i+1.
        // After each middleware returns, Blocked is latched into the sticky flag;
        // once latched (or observed live), next becomes a no-op so a blocking
        // middleware can never pull downstream middlewares through the chain.
        ExtensionNext NextFor(int index)
            => index == context.Middleware.Count - 1
                ? (_, _) =>
                {
                    blocked = blocked || middlewareContext.Blocked;
                    return Task.CompletedTask;
                }
                : async (nextContext, token) =>
                {
                    if (blocked || middlewareContext.Blocked)
                    {
                        blocked = true;
                        return;
                    }

                    await context.Middleware[index + 1].Value(nextContext, NextFor(index + 1), token);
                    blocked = blocked || middlewareContext.Blocked;
                };
    }
}
