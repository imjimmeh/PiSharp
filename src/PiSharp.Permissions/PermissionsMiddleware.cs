using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// <summary>
/// The tool-call middleware (P29 §3.1) enforcing the permission gate at the single
/// <c>Use(...)</c> seam: explicit session grants win immediately, then the compiled policy
/// (matrix + dangerous defaults + mode), then interactive approval over the
/// <c>permission_request</c> UI lane for Ask decisions. Denied calls set
/// <see cref="ExtensionMiddlewareContext.Blocked"/> + <see cref="ExtensionMiddlewareContext.BlockReason"/>
/// without invoking <c>next</c>, so the harness surfaces the block to the model as a tool error.
/// </summary>
public sealed class PermissionsMiddleware
{
    private readonly IExtensionApi _api;
    private readonly Func<PermissionsPolicy> _policy;
    private readonly GrantStore _grants;
    private readonly ApprovalClient _approvals;
    private readonly AuditRecorder _audit;
    private readonly Func<string, CancellationToken, Task<string>> _sessionKeyResolver;

    public PermissionsMiddleware(
        IExtensionApi api,
        Func<PermissionsPolicy> policy,
        GrantStore grants,
        ApprovalClient approvals,
        AuditRecorder audit,
        Func<string, CancellationToken, Task<string>>? sessionKeyResolver = null)
    {
        _api = api;
        _policy = policy;
        _grants = grants;
        _approvals = approvals;
        _audit = audit;
        _sessionKeyResolver = sessionKeyResolver ?? ((_, ct) => SessionKeys.ResolveAsync(_api, ct));
    }

    public async Task InvokeAsync(ExtensionMiddlewareContext context, ExtensionNext next, CancellationToken cancellationToken)
    {
        var before = context.BeforeToolCall;
        if (before is null)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tool = before.ToolCall.Name;
        var args = before.Args;
        var serialized = DangerousOpDetector.Serialize(args);
        var sessionKey = await _sessionKeyResolver(tool, cancellationToken).ConfigureAwait(false);
        var policy = _policy();

        // 1. Explicit session grant wins immediately (P29 §3.3 step 1).
        var grant = await _grants.FindAsync(tool, sessionKey, serialized, cancellationToken).ConfigureAwait(false);
        if (grant is not null)
        {
            if (grant.Action == PermissionAction.Deny)
            {
                var reason = $"Permission denied for '{tool}' by a session grant.";
                context.Blocked = true;
                context.BlockReason = reason;
                if (policy.Audit) await _audit.RecordBlockedAsync(tool, reason, sessionKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Allow grant: the session already approved this tool call — bypass the policy gate.
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 2. Static matrix + dangerous defaults + mode posture.
        var category = await DangerousOpDetector.CategoryAsync(tool, args, _api.ExecutionEnv, cancellationToken).ConfigureAwait(false);
        var decision = policy.Evaluate(tool, serialized, category, !_api.HasUi);

        if (decision.Action == PermissionAction.Deny)
        {
            context.Blocked = true;
            context.BlockReason = decision.Reason;
            if (policy.Audit) await _audit.RecordBlockedAsync(tool, decision.Reason, sessionKey, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (decision.Action == PermissionAction.Ask)
        {
            var verdict = await _approvals.RequestApprovalAsync(tool, args, decision.Reason, sessionKey, cancellationToken).ConfigureAwait(false);
            switch (verdict)
            {
                case ApprovalVerdict.Deny:
                    context.Blocked = true;
                    context.BlockReason = $"Permission denied for '{tool}': {decision.Reason}";
                    if (policy.Audit) await _audit.RecordBlockedAsync(tool, context.BlockReason, sessionKey, cancellationToken).ConfigureAwait(false);
                    return;

                case ApprovalVerdict.AllowSession:
                    var expiresAt = System.DateTimeOffset.UtcNow.AddSeconds(policy.GrantTtlSeconds);
                    await _grants.StoreAsync(new PermissionGrant(sessionKey, tool, null, PermissionAction.Allow, expiresAt), cancellationToken).ConfigureAwait(false);
                    if (policy.Audit) await _audit.RecordApprovedAsync(tool, sessionKey, policy.GrantTtlSeconds, cancellationToken).ConfigureAwait(false);
                    break;

                default: // Allow
                    if (policy.Audit) await _audit.RecordApprovedAsync(tool, sessionKey, null, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Resolves the grant-scoping session key (session name, falling back to "default").</summary>
internal static class SessionKeys
{
    public static async Task<string> ResolveAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        try
        {
            var name = await api.Session.GetNameAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception)
        {
            // Session name unavailable — fall back to "default" (mirrors the plan-mode plugin).
        }
        return "default";
    }
}
