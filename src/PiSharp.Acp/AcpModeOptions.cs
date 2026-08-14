namespace PiSharp.Acp;

/// <summary>
/// Configuration for the ACP mode server, matching plan §4.2. The CLI glue
/// resolves approval mode precedence (<c>--approval-mode</c> &gt; settings &gt; default)
/// and passes the resolved value here.
/// </summary>
public sealed record AcpModeOptions(
    AcpApprovalMode ApprovalMode = AcpApprovalMode.Ask,
    IReadOnlyList<string>? PermissionAllowlist = null)
{
    /// <summary>Default read-only allowlist (plan §3.5/§9).</summary>
    public static IReadOnlyList<string> DefaultAllowlist { get; } = ["read", "grep", "find", "ls"];
}
