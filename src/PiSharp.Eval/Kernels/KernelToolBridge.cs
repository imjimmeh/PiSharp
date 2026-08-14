using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Agent.Core.Tools;
using PiSharp.Eval.Events;
using PiSharp.Extensions;
using PiSharp.Abstractions.Messages;

namespace PiSharp.Eval.Kernels;

/// <summary>
/// Loopback bridge: executes the agent's own tools by name from kernel code, with an
/// allowlist (default <c>read/grep/find/ls</c>). Blocked calls return a normal error result,
/// never an exception. Loopback calls bypass the harness event pipeline (no
/// <c>tool_call</c>/<c>tool_execution_*</c> events — the model did not initiate them);
/// observability rides the dedicated <c>eval_loopback_tool_call</c>/<c>eval_loopback_tool_result</c>
/// events instead.
/// </summary>
public sealed class KernelToolBridge : IKernelToolBridge
{
    private const int MaxOutputBytes = 256 * 1024;
    private const string TruncationMarker = "\n... [loopback output truncated]";

    private readonly string _kernelName;
    private readonly IExtensionToolApi _tools;
    private readonly Func<string, object?, CancellationToken, Task> _emit;
    private readonly Func<string> _toolCallIdFactory;
    private long _callCounter;
    private IReadOnlyList<string> _allowlist;

    public KernelToolBridge(
        string kernelName,
        IExtensionToolApi tools,
        Func<string, object?, CancellationToken, Task> emit,
        IReadOnlyList<string> allowlist,
        Func<string>? toolCallIdFactory = null)
    {
        _kernelName = kernelName;
        _tools = tools;
        _emit = emit;
        _allowlist = allowlist;
        _toolCallIdFactory = toolCallIdFactory ?? (() => $"eval-loopback:{Interlocked.Increment(ref _callCounter)}");
    }

    public IReadOnlyList<string> AvailableToolNames
    {
        get { lock (this) return _allowlist.ToArray(); }
    }

    /// <summary>Hot-reload entry: replaces the allowlist (settings changed).</summary>
    public void UpdateAllowlist(IReadOnlyList<string> allowlist)
    {
        lock (this) _allowlist = allowlist;
    }

    public async Task<KernelToolResult> ExecuteToolAsync(string toolName, JsonElement parameters, CancellationToken ct = default)
    {
        IReadOnlyList<string> allowlist;
        lock (this) allowlist = _allowlist;

        if (!allowlist.Contains(toolName, StringComparer.Ordinal))
        {
            return new KernelToolResult(
                false,
                $"tool '{toolName}' is not allowed for eval loopback. Allowed tools: {string.Join(", ", allowlist)}.",
                null, null, false);
        }

        var toolCallId = _toolCallIdFactory();
        await EmitAsync(EvalEventNames.LoopbackToolCall,
            new EvalLoopbackToolCallEvent(_kernelName, toolName, parameters, toolCallId), ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _tools.ExecuteToolAsync(toolName, parameters, ct);
            sw.Stop();
            var text = JoinContent(result.Content);
            var truncated = TruncateIfNeeded(ref text);
            var ok = !LooksLikeError(text);
            await EmitAsync(EvalEventNames.LoopbackToolResult,
                new EvalLoopbackToolResultEvent(_kernelName, toolName, toolCallId, ok,
                    ok ? null : text, sw.Elapsed.TotalMilliseconds, truncated), ct);
            return new KernelToolResult(ok, ok ? null : text, text, result.Details, truncated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var error = $"loopback tool '{toolName}' failed: {ex.Message}";
            await EmitAsync(EvalEventNames.LoopbackToolResult,
                new EvalLoopbackToolResultEvent(_kernelName, toolName, toolCallId, false, error,
                    sw.Elapsed.TotalMilliseconds, false), ct);
            return new KernelToolResult(false, error, null, null, false);
        }
    }

    private async Task EmitAsync(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _emit(eventName, payload, ct);
        }
        catch (Exception)
        {
            // Event emission must never break the loopback call.
        }
    }

    private static string JoinContent(IReadOnlyList<MessageContent>? content)
    {
        if (content is null || content.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var item in content)
        {
            if (item is PiSharp.Abstractions.Messages.TextContent text)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    private static bool LooksLikeError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("Error:", StringComparison.Ordinal) ||
               trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Unhandled exception", StringComparison.Ordinal);
    }

    private static bool TruncateIfNeeded(ref string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= MaxOutputBytes) return false;
        // Cut on a UTF-8 character boundary near the cap.
        var end = Math.Min(text.Length, MaxOutputBytes);
        while (end > 0 && (text[end - 1] & 0xC0) == 0x80) end--;
        text = text[..end] + TruncationMarker;
        return true;
    }
}
