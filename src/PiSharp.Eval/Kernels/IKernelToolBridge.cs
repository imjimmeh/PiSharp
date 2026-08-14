using System.Text.Json;

namespace PiSharp.Eval.Kernels;

/// <summary>
/// Result of a loopback tool call: kernel code executing the agent's own tools by name.
/// Blocked (non-allowlisted) calls return a normal error result, never an exception.
/// </summary>
public sealed record KernelToolResult(
    bool Ok,
    string? Error,
    object? Content,          // the IAgentTool result Content (string for built-ins)
    object? Details,
    bool Truncated);

/// <summary>
/// Tool re-entry surface available to kernel code. Implemented by
/// <see cref="KernelToolBridge"/> over the host's execute-tool-by-name delegate
/// (<c>IExtensionToolApi.ExecuteToolAsync</c>).
/// </summary>
public interface IKernelToolBridge
{
    IReadOnlyList<string> AvailableToolNames { get; }   // current allowlist (settings-resolved)
    Task<KernelToolResult> ExecuteToolAsync(string toolName, JsonElement parameters,
        CancellationToken ct = default);
}
