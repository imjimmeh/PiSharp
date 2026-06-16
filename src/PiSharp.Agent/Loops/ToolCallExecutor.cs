using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Agent.Loops;

public sealed record ExecutedToolCallBatch(IReadOnlyList<ToolResultMessage> Messages, bool Terminate);

internal sealed record FinalizedToolCall(ToolCallContent ToolCall, AgentToolResult<object?> Result, bool IsError);

public static class ToolCallExecutor
{
    private static ILogger _logger = NullLogger.Instance;

    public static void SetLogger(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger("PiSharp.Agent.Loops.ToolCallExecutor") ?? NullLogger.Instance;
    }
    public static Task<ExecutedToolCallBatch> ExecuteAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken)
        => ExecuteAsync(context, assistantMessage, config, (evt, _) =>
        {
            emit(evt);
            return Task.CompletedTask;
        }, cancellationToken);

    public static async Task<ExecutedToolCallBatch> ExecuteAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        var toolCalls = assistantMessage.Content.OfType<ToolCallContent>().ToArray();
        var hasSequentialTool = toolCalls.Any(call => context.Tools?.FirstOrDefault(tool => tool.Name == call.Name)?.ExecutionMode == ToolExecutionMode.Sequential);
        return config.ToolExecution == ToolExecutionMode.Sequential || hasSequentialTool
            ? await ExecuteSequentialAsync(context, assistantMessage, toolCalls, config, emitAsync, cancellationToken)
            : await ExecuteParallelAsync(context, assistantMessage, toolCalls, config, emitAsync, cancellationToken);
    }

    private static async Task<ExecutedToolCallBatch> ExecuteSequentialAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        var finalized = new List<FinalizedToolCall>();
        var messages = new List<ToolResultMessage>();
        foreach (var toolCall in toolCalls)
        {
            await emitAsync(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments), cancellationToken);
            var call = await PrepareExecuteFinalizeAsync(context, assistantMessage, toolCall, config, emitAsync, cancellationToken);
            await emitAsync(new AgentEvent.ToolExecutionEnd(call.ToolCall.Id, call.ToolCall.Name, call.Result, call.IsError), cancellationToken);
            var resultMessage = CreateToolResultMessage(call);
            await emitAsync(new AgentEvent.MessageStart(resultMessage), cancellationToken);
            await emitAsync(new AgentEvent.MessageEnd(resultMessage), cancellationToken);
            finalized.Add(call);
            messages.Add(resultMessage);
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminate(finalized));
    }

    private static async Task<ExecutedToolCallBatch> ExecuteParallelAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        var factories = new List<Func<Task<FinalizedToolCall>>>();
        foreach (var toolCall in toolCalls)
        {
            await emitAsync(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments), cancellationToken);
            factories.Add(async () =>
            {
                var call = await PrepareExecuteFinalizeAsync(context, assistantMessage, toolCall, config, emitAsync, cancellationToken);
                await emitAsync(new AgentEvent.ToolExecutionEnd(call.ToolCall.Id, call.ToolCall.Name, call.Result, call.IsError), cancellationToken);
                return call;
            });
        }

        var finalized = await Task.WhenAll(factories.Select(factory => factory()));
        var messages = new List<ToolResultMessage>();
        foreach (var call in finalized)
        {
            var resultMessage = CreateToolResultMessage(call);
            await emitAsync(new AgentEvent.MessageStart(resultMessage), cancellationToken);
            await emitAsync(new AgentEvent.MessageEnd(resultMessage), cancellationToken);
            messages.Add(resultMessage);
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminate(finalized));
    }

    private static async Task<FinalizedToolCall> PrepareExecuteFinalizeAsync(
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCallContent toolCall,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        var tool = context.Tools?.FirstOrDefault(candidate => candidate.Name == toolCall.Name);
        if (tool is null) return new FinalizedToolCall(toolCall, ErrorResult($"Tool {toolCall.Name} not found"), true);

        try
        {
            var args = tool.PrepareArguments(toolCall.Arguments);
            if (config.BeforeToolCall is not null)
            {
                var before = await config.BeforeToolCall(new BeforeToolCallContext(assistantMessage, toolCall, args, context), cancellationToken);
                if (before?.Block == true) return new FinalizedToolCall(toolCall, ErrorResult(before.Reason ?? "Tool execution was blocked"), true);
            }

            var result = await tool.ExecuteAsync(toolCall.Id, args, cancellationToken, partial =>
            {
                _ = emitAsync(new AgentEvent.ToolExecutionUpdate(toolCall.Id, toolCall.Name, toolCall.Arguments, partial), cancellationToken);
            });
            var isError = false;
            if (config.AfterToolCall is not null)
            {
                try
                {
                    var patch = await config.AfterToolCall(new AfterToolCallContext(assistantMessage, toolCall, args, result, isError, context), cancellationToken);
                    if (patch is not null)
                    {
                        result = result with { Content = patch.Content ?? result.Content, Details = patch.Details ?? result.Details, Terminate = patch.Terminate ?? result.Terminate };
                        isError = patch.IsError ?? isError;
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Tool {ToolName} execution failed", toolCall.Name);
                    result = ErrorResult(exception.Message);
                    isError = true;
                }
            }

            return new FinalizedToolCall(toolCall, result, isError);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tool call executor failed");
            return new FinalizedToolCall(toolCall, ErrorResult(exception.Message), true);
        }
    }

    private static AgentToolResult<object?> ErrorResult(string message)
        => new([new TextContent(message)], new { }, false);

    private static ToolResultMessage CreateToolResultMessage(FinalizedToolCall call)
        => new(call.ToolCall.Id, call.ToolCall.Name, call.Result.Content, call.Result.Details, call.IsError);

    private static bool ShouldTerminate(IEnumerable<FinalizedToolCall> calls)
    {
        var materialized = calls.ToArray();
        return materialized.Length > 0 && materialized.All(call => call.Result.Terminate);
    }
}
