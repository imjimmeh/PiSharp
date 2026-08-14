using System.Text.Json;

namespace PiSharp.Eval.Events;

/// <summary>
/// Plugin-emitted event names and payload records (snake_case payloads, same style as
/// <c>ExtensionEventNames</c>). Consumers: daemon/TUI clients and P25 observability.
/// </summary>
public static class EvalEventNames
{
    public const string KernelStart = "eval_kernel_start";
    public const string KernelEnd = "eval_kernel_end";
    public const string KernelReset = "eval_kernel_reset";
    public const string LoopbackToolCall = "eval_loopback_tool_call";
    public const string LoopbackToolResult = "eval_loopback_tool_result";
    public const string Snapshot = "eval_snapshot";
    public const string Restore = "eval_restore";
    public const string BenchRunStart = "bench_run_start";
    public const string BenchCaseStart = "bench_case_start";
    public const string BenchCaseEnd = "bench_case_end";
    public const string BenchRunEnd = "bench_run_end";
}

public sealed record EvalKernelStartEvent(string Kernel, string? SessionId, bool Restored);
public sealed record EvalKernelEndEvent(string Kernel, string? SessionId, string Reason);
public sealed record EvalKernelResetEvent(string Kernel, string? SessionId, string Reason);   // "explicit" | "timeout" | "error"
public sealed record EvalLoopbackToolCallEvent(string Kernel, string ToolName, JsonElement Args, string ToolCallId);
public sealed record EvalLoopbackToolResultEvent(
    string Kernel, string ToolName, string ToolCallId, bool Ok, string? Error, double DurationMs, bool Truncated);
public sealed record EvalSnapshotEvent(string Kernel, string? SessionId, bool Lossy, int VariableCount, long Bytes);
public sealed record EvalRestoreEvent(string Kernel, string? SessionId, bool Lossy, int VariableCount);

public sealed record BenchRunStartEvent(string RunId, string SpecName, int Runs, int CaseCount, DateTimeOffset StartedAt);
public sealed record BenchCaseStartEvent(string RunId, string CaseName, int RunIndex, string? Agent, string? Kernel);
public sealed record BenchCaseEndEvent(
    string RunId, string CaseName, int RunIndex, bool Passed, double? Score, int Tokens, decimal? Cost,
    double LatencyMs, string? Error);
public sealed record BenchRunEndEvent(
    string RunId, string SpecName, int Passed, int Total, double PassRate, double LatencyMs, int Tokens,
    decimal? Cost, string? ResultFile);
