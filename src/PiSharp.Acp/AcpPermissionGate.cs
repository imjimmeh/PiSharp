using PiSharp.Extensions;

namespace PiSharp.Acp;

/// <summary>
/// Resolves inbound approval round-trips for the permission gate (plan §5.2). The server
/// implements this and registers a pending TCS per <c>session/request_permission</c> it sends;
/// the read loop resolves them when the client answers (or on cancel / connection death).
/// </summary>
public interface IAcpPermissionResponder
{
    Task<AcpPermissionOutcome> RequestAsync(AcpToolCallUpdate toolCall, CancellationToken cancellationToken);

    void RejectAllPending(string reason);
}

/// <summary>Options for <see cref="AcpPermissionGate.Create"/>.</summary>
public sealed record AcpPermissionGateOptions(
    AcpApprovalMode ApprovalMode,
    IReadOnlyList<string>? Allowlist = null,
    IAcpPermissionResponder? Responder = null,
    Func<string, (string Title, string Kind)>? ToolMeta = null)
{
    public static IReadOnlyList<string> DefaultAllowlist { get; } = ["read", "grep", "find", "ls"];
}

/// <summary>
/// Builds an <see cref="ExtensionMiddleware"/> enforcing the approval policy (plan §3.5/§4.2) for
/// non-allowlisted tool calls. The middleware runs before every tool call via the harness's
/// <c>BeforeToolCall</c> wiring. Blocking is expressed by setting <c>Blocked</c>/<c>BlockReason</c>
/// on the <see cref="ExtensionMiddlewareContext"/>. In <c>ask</c> mode a non-allowlisted tool pauses
/// the turn until <see cref="IAcpPermissionResponder.RequestAsync"/> returns an outcome.
/// </summary>
public static class AcpPermissionGate
{
    public static ExtensionMiddleware Create(AcpPermissionGateOptions options)
    {
        var allowlist = new HashSet<string>(options.Allowlist ?? AcpPermissionGateOptions.DefaultAllowlist, StringComparer.Ordinal);
        var toolMeta = options.ToolMeta ?? (name => (name, AcpEventTranslator.MapToolKind(name)));

        return (context, next, cancellationToken) =>
        {
            // Only the before-tool phase is a gate; other phases pass through.
            if (context.BeforeToolCall is null)
                return next(context, cancellationToken);

            var tool = context.BeforeToolCall.ToolCall;
            switch (options.ApprovalMode)
            {
                case AcpApprovalMode.Yolo:
                    return next(context, cancellationToken);

                case AcpApprovalMode.ReadOnly:
                    if (allowlist.Contains(tool.Name)) return next(context, cancellationToken);
                    context.Blocked = true;
                    context.BlockReason = "read-only approval mode";
                    return Task.CompletedTask;

                case AcpApprovalMode.Ask:
                    if (allowlist.Contains(tool.Name)) return next(context, cancellationToken);
                    return AskAsync(context, next, tool, options.Responder, toolMeta, cancellationToken);

                default:
                    return next(context, cancellationToken);
            }
        };
    }

    private static async Task AskAsync(
        ExtensionMiddlewareContext context,
        ExtensionNext next,
        PiSharp.Abstractions.Messages.ToolCallContent tool,
        IAcpPermissionResponder? responder,
        Func<string, (string Title, string Kind)> toolMeta,
        CancellationToken cancellationToken)
    {
        var (title, kind) = toolMeta(tool.Name);
        var update = new AcpToolCallUpdate(tool.Id, title, kind, "pending", RawInput: tool.Arguments);

        // Without a responder (no live ACP connection), degrade to allow (plan §3.3 note).
        if (responder is null)
        {
            await next(context, cancellationToken);
            return;
        }

        AcpPermissionOutcome outcome;
        try
        {
            outcome = await responder.RequestAsync(update, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            context.Blocked = true;
            context.BlockReason = "cancelled";
            return;
        }

        if (outcome.Outcome == "selected" && outcome.OptionId == "allow-once")
        {
            await next(context, cancellationToken);
            return;
        }

        // reject-once or cancelled → block the tool call.
        context.Blocked = true;
        context.BlockReason = outcome.Outcome == "cancelled" ? "cancelled" : "rejected by user";
    }
}
