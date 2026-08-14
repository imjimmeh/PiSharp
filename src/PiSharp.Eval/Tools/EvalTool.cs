using System.ComponentModel;
using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using PiSharp.Eval.Events;
using PiSharp.Eval.Kernels;
using PiSharp.Extensions;
using PiSharp.Tools;

namespace PiSharp.Eval.Tools;

public sealed record EvalToolInput(
    [property: Description("Source code to execute in the persistent kernel.")]
    string Code,

    [property: Description("Kernel name; defaults to eval.kernel.default.")]
    string? Kernel = null,

    [property: Description("Execution timeout in milliseconds (default eval.kernel.timeoutMs).")]
    int? TimeoutMs = null,

    [property: Description("Reset the kernel to a fresh state before executing.")]
    bool Reset = false);

/// <summary>
/// The <c>eval</c> tool (model-callable): executes code in the session's persistent kernel.
/// Execution is serialized per kernel (the model cannot interleave state). Result is a JSON
/// summary <c>{ output, isError, durationMs, timedOut, wasReset, kernel, snapshotLossy? }</c>.
/// </summary>
public sealed class EvalTool
{
    public const string Name = "eval";

    private readonly KernelRegistry _registry;
    private readonly Func<string, object?, CancellationToken, Task> _emit;
    private readonly string _sessionId;
    private readonly string _defaultKernel;
    private readonly int _defaultTimeoutMs;

    public EvalTool(
        KernelRegistry registry,
        Func<string, object?, CancellationToken, Task> emit,
        string sessionId,
        string defaultKernel,
        int defaultTimeoutMs)
    {
        _registry = registry;
        _emit = emit;
        _sessionId = sessionId;
        _defaultKernel = defaultKernel;
        _defaultTimeoutMs = defaultTimeoutMs;
    }

    public ExtensionToolRegistration ToRegistration() => new(
        Name,
        "eval",
        "Execute source code in the persistent eval kernel. Kernel state persists across calls until reset. Use for computation, data munging, or reproducible evaluation.",
        ToolSchemas.FromType<EvalToolInput>(),
        ExecuteAsync,
        ExecutionMode: ToolExecutionMode.Sequential,
        PromptSnippet: "Run code in the persistent eval kernel (state persists between calls)",
        PromptGuidelines:
        [
            "Kernel state persists across eval calls; reuse variables instead of recomputing.",
            "Set Reset=true to start from a fresh kernel state.",
            "A kernel that times out is poisoned and resets automatically on the next call (wasReset=true).",
        ]);

    private async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken,
        AgentToolUpdateCallback<object?>? onUpdate)
    {
        var input = parameters.Deserialize<EvalToolInput>(WebJsonOptions)
            ?? new EvalToolInput(string.Empty);
        var kernelName = input.Kernel ?? _defaultKernel;

        try
        {
            var result = await _registry.ExecuteAsync(
                kernelName,
                input.Code,
                new KernelExecuteOptions(input.TimeoutMs ?? _defaultTimeoutMs, input.Reset),
                cancellationToken);

            if (result.WasReset)
            {
                await Emit(EvalEventNames.KernelReset,
                    new EvalKernelResetEvent(kernelName, _sessionId, "timeout"), cancellationToken);
            }
            else if (input.Reset)
            {
                await Emit(EvalEventNames.KernelReset,
                    new EvalKernelResetEvent(kernelName, _sessionId, "explicit"), cancellationToken);
            }

            var payload = new
            {
                output = result.Output,
                isError = result.IsError,
                durationMs = result.DurationMs,
                timedOut = result.TimedOut,
                wasReset = result.WasReset,
                kernel = kernelName,
                snapshotLossy = result.Snapshot?.Lossy,
            };
            var text = JsonSerializer.Serialize(payload, IndentedWebJsonOptions);
            return new AgentToolResult<object?>([new PiSharp.Abstractions.Messages.TextContent(text)], null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult<object?>(
                [new PiSharp.Abstractions.Messages.TextContent($"Error: eval failed: {ex.Message}")], null);
        }
    }

    private async Task Emit(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            await _emit(eventName, payload, ct);
        }
        catch (Exception)
        {
            // Event emission must never break the eval call.
        }
    }

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions IndentedWebJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
