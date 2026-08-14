using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

namespace PiSharp.Plugins.Debug;

/// <summary>
/// The <c>debug</c> tool handler. Sessions live in the registry across calls; ops other
/// than <c>attach</c>/<c>list</c> address a session by id. Every op returns structured
/// details plus a readable summary.
/// </summary>
public sealed class DebugToolHandler
{
    private readonly Func<bool> _isEnabled;
    private readonly string _cwd;
    private readonly ILogger _logger;
    private DebugSessionRegistry _registry;

    public DebugToolHandler(
        DebugSessionRegistry registry,
        Func<bool> isEnabled,
        string cwd,
        ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _isEnabled = isEnabled;
        _cwd = cwd;
        _logger = loggerFactory?.CreateLogger<DebugToolHandler>() ?? NullLogger<DebugToolHandler>.Instance;
    }

    /// <summary>Points at a replacement registry after settings hot-reload.</summary>
    public void SwapRegistry(DebugSessionRegistry registry) => _registry = registry;

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!_isEnabled())
        {
            return ErrorResult("debug is disabled; enable it via settings 'extensions.pisharp-debug.enabled' (default: false).");
        }

        var input = JsonSerializer.Deserialize<DebugToolInput>(parameters.GetRawText());
        if (input is null)
        {
            return ErrorResult("Invalid debug tool arguments: expected an object with 'op'.");
        }

        var stopwatch = Stopwatch.StartNew();
        DebugToolDetails details;
        string content;
        try
        {
            var execution = await ExecuteOpAsync(input, cancellationToken).ConfigureAwait(false);
            details = execution.Details;
            content = execution.Content;
        }
        catch (JsonRpcRemoteException exception)
        {
            stopwatch.Stop();
            details = new DebugToolDetails(input.Op, input.SessionId, input.Language, null, null, null, exception.Message);
            content = $"debug {input.Op} failed: {exception.Message}";
        }
        catch (KeyNotFoundException exception)
        {
            stopwatch.Stop();
            details = new DebugToolDetails(input.Op, input.SessionId, input.Language, null, null, null, exception.Message);
            content = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TaskCanceledException or TimeoutException)
        {
            stopwatch.Stop();
            details = new DebugToolDetails(input.Op, input.SessionId, input.Language, null, null, null, exception.Message);
            content = $"debug {input.Op} failed: {exception.Message}";
        }

        return new AgentToolResult<object?>([new TextContent(content)], details);
    }

    private async Task<(DebugToolDetails Details, string Content)> ExecuteOpAsync(DebugToolInput input, CancellationToken cancellationToken)
    {
        var registry = _registry;
        switch (input.Op)
        {
            case "attach":
            {
                if (string.IsNullOrWhiteSpace(input.Language))
                {
                    throw new KeyNotFoundException("debug attach requires a 'language' id. Configured adapters: " + string.Join(", ", registry.Languages) + ".");
                }

                var session = await registry.AttachAsync(input.Language, input, cancellationToken).ConfigureAwait(false);
                return (
                    new DebugToolDetails(input.Op, session.SessionId, session.Language, session, null, Describe(session), null),
                    $"Attached debug session `{session.SessionId}` (language `{session.Language}`).\n\n{Describe(session)}\n\nUse the returned session id for subsequent debug ops.");
            }

            case "list":
            {
                var sessions = registry.List();
                var content = sessions.Count == 0
                    ? "No debug sessions."
                    : string.Join("\n", sessions.Select(Describe));
                return (new DebugToolDetails(input.Op, null, null, null, null, content, null), content);
            }

            case "disconnect":
            {
                var sessionId = RequireSessionId(input);
                await registry.DisconnectAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return (
                    new DebugToolDetails(input.Op, sessionId, null, null, null, $"Disconnected debug session `{sessionId}`.", null),
                    $"Disconnected debug session `{sessionId}`.");
            }
        }

        var sessionIdForOp = RequireSessionId(input);
        var client = await registry.GetSessionAsync(sessionIdForOp, cancellationToken).ConfigureAwait(false);
        switch (input.Op)
        {
            case "continue":
            {
                var result = await client.ContinueAsync(input.ThreadId, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"continued (thread {input.ThreadId?.ToString() ?? "all"})");
            }

            case "pause":
            {
                var threadId = input.ThreadId ?? throw new KeyNotFoundException("debug pause requires a 'threadId'.");
                var result = await client.PauseAsync(threadId, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"paused thread {threadId}");
            }

            case "step":
            {
                var threadId = input.ThreadId ?? throw new KeyNotFoundException("debug step requires a 'threadId'.");
                var result = await client.StepAsync(threadId, input.StepType ?? "next", cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"stepped {input.StepType ?? "next"} on thread {threadId}");
            }

            case "threads":
            {
                var result = await client.ThreadsAsync(cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, "threads");
            }

            case "stackTrace":
            {
                var threadId = input.ThreadId ?? throw new KeyNotFoundException("debug stackTrace requires a 'threadId'.");
                var result = await client.StackTraceAsync(threadId, input.Count, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"stack trace for thread {threadId}");
            }

            case "scopes":
            {
                var frameId = input.FrameId ?? throw new KeyNotFoundException("debug scopes requires a 'frameId'.");
                var result = await client.ScopesAsync(frameId, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"scopes for frame {frameId}");
            }

            case "variables":
            {
                var variablesReference = input.VariablesReference ?? throw new KeyNotFoundException("debug variables requires a 'variablesReference'.");
                var result = await client.VariablesAsync(variablesReference, input.Start, input.Count, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"variables {variablesReference}");
            }

            case "evaluate":
            {
                if (string.IsNullOrWhiteSpace(input.Expression))
                {
                    throw new KeyNotFoundException("debug evaluate requires an 'expression'.");
                }

                var result = await client.EvaluateAsync(input.Expression, input.FrameId, input.Context, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"evaluated `{input.Expression}`");
            }

            case "setBreakpoints":
            {
                if (string.IsNullOrWhiteSpace(input.Path))
                {
                    throw new KeyNotFoundException("debug setBreakpoints requires a 'path'.");
                }

                var lines = input.Lines ?? [];
                var result = await client.SetBreakpointsAsync(input.Path, lines, cancellationToken).ConfigureAwait(false);
                return ForResult(input, sessionIdForOp, result, $"set {lines.Count} breakpoint(s) on {input.Path}");
            }

            default:
                throw new KeyNotFoundException(
                    $"Unknown debug op '{input.Op}'. Ops: attach, continue, pause, step, threads, stackTrace, scopes, variables, evaluate, setBreakpoints, disconnect, list.");
        }
    }

    private static (DebugToolDetails Details, string Content) ForResult(DebugToolInput input, string sessionId, JsonElement result, string action)
    {
        var summary = $"debug {action}: {JsonSerializer.Serialize(result)}";
        return (new DebugToolDetails(input.Op, sessionId, null, null, result.Clone(), summary, null), summary);
    }

    private static string RequireSessionId(DebugToolInput input)
        => string.IsNullOrWhiteSpace(input.SessionId)
            ? throw new KeyNotFoundException($"debug {input.Op} requires a 'sessionId' from a prior attach.")
            : input.SessionId;

    private static string Describe(DebugSessionInfo session)
    {
        var state = session.State;
        if (session.LastError is { } error)
        {
            state += $" ({error})";
        }

        return $"`{session.SessionId}` · {session.Language} · {state}" + (session.DebuggeeLabel is null ? "" : $" · {session.DebuggeeLabel}");
    }

    private static AgentToolResult<object?> ErrorResult(string message)
        => new([new TextContent(message)], new DebugToolDetails("", null, null, null, null, null, message));
}
