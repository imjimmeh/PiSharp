namespace PiSharp.Acp;

/// <summary>
/// Approval policy for non-allowlisted tool calls, mirroring the CLI's
/// <c>--approval-mode</c> choices. PiSharp.Acp defines its own copy because it
/// cannot reference PiSharp.Cli (which owns the CLI-plumbing enum); the thin
/// <c>AcpMode</c> glue maps between the two at the call boundary.
/// </summary>
public enum AcpApprovalMode
{
    /// <summary>Never block tool calls; no permission round-trip.</summary>
    Yolo,

    /// <summary>Ask the editor for non-allowlisted tool calls via <c>session/request_permission</c>.</summary>
    Ask,

    /// <summary>Allow only the allowlist; everything else is blocked without asking.</summary>
    ReadOnly
}
