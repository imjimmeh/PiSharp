using System.Text.Json.Serialization;

namespace PiSharp.Permissions;

/// <summary>
/// Resolution of a model tool call against the permission policy.
/// <c>Ask</c> means "raise an approval prompt" (or resolve via the mode posture).
/// </summary>
public enum PermissionAction
{
    Allow,
    Ask,
    Deny
}

/// <summary>
/// A policy rule: a tool name plus an optional case-insensitive regex matched against the
/// JSON-serialized tool arguments. The JSON keys follow the settings schema
/// (<c>extensions.pisharp-permissions.allow/deny/ask</c>).
/// </summary>
public sealed record PermissionRule(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("pattern")] string? Pattern = null);

/// <summary>
/// Outcome of evaluating a tool call against the policy.
/// <see cref="Reason"/> is the human/model-visible explanation when the action is Deny or Ask;
/// <see cref="MatchedRule"/> records the source of the decision for auditing.
/// </summary>
public sealed record PermissionDecision(
    PermissionAction Action,
    string Reason,
    string? MatchedRule = null);

/// <summary>
/// A session-persisted grant ("allow for this session" or an explicit deny) for a tool,
/// time-bounded via <see cref="ExpiresAt"/>. Only Allow/Deny grants are stored (Ask grants are
/// not persisted).
/// </summary>
public sealed record PermissionGrant(
    string SessionKey,
    string Tool,
    string? Pattern,
    PermissionAction Action,
    DateTimeOffset ExpiresAt);

/// <summary>
/// User verdict for a <c>permission_request</c> approval prompt, or the headless auto-decline.
/// </summary>
public enum ApprovalVerdict
{
    Allow,
    Deny,
    AllowSession
}
