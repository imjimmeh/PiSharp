using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Tools;
using PiSharp.Runtime;

namespace PiSharp.Acp;

/// <summary>Options for the ACP server (plan §4.2).</summary>
public sealed record AcpServerOptions(
    AcpSessionManager SessionManager,
    AcpModeOptions Mode,
    IReadOnlyList<IAgentTool>? Tools = null,
    ILoggerFactory? LoggerFactory = null);

/// <summary>
/// In-process ACP (Agent Client Protocol) JSON-RPC 2.0 server over newline-delimited stdio
/// (plan §2/§3.2). Owns the read/dispatch/write loop over <see cref="TextReader"/>/<see cref="TextWriter"/>,
/// a single-writer gate, the method registry, notification fan-out, harness-event streaming, and
/// connection-death handling (pending permission TCSs are released with <c>cancelled</c>).
/// </summary>
public sealed class AcpServer : IAcpPermissionResponder, IAsyncDisposable
{
    private const int ProtocolVersion = 1;

    private readonly AcpSessionManager _sessions;
    private readonly AcpModeOptions _mode;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly ILogger _logger;
    private readonly object _pendingGate = new();
    private readonly Dictionary<long, TaskCompletionSource<AcpPermissionOutcome>> _pendingPermissions = new();
    private readonly List<Task> _turnTasks = [];
    private readonly object _rebindGate = new();

    private AcpMessageWriter _writer = null!;
    private AcpEventTranslator? _translator;
    private IDisposable? _subscription;
    private IDisposable? _middlewareHandle;
    private long _nextRequestId;

    public AcpServer(AcpServerOptions options)
    {
        _sessions = options.SessionManager;
        _mode = options.Mode;
        _tools = options.Tools ?? [];
        _logger = options.LoggerFactory?.CreateLogger<AcpServer>() ?? NullLogger<AcpServer>.Instance;
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        _writer = new AcpMessageWriter(output);
        _sessions.AttachResponder(this);
        RegisterMiddleware();
        BindCurrentHarness();
        _sessions.Runtime.SetRebindSession((_, _) => { BindCurrentHarness(); return Task.CompletedTask; });

        try
        {
            string? line;
            while ((line = await input.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                await RouteAsync(line, cancellationToken);
            }

            // Connection death: release pending permission requests with the safe reject default.
            RejectAllPending("connection closed");
            await Task.WhenAll(_turnTasks);
        }
        finally
        {
            _subscription?.Dispose();
            _middlewareHandle?.Dispose();
            RejectAllPending("connection closed");
        }
    }

    #region IAcpPermissionResponder

    public Task<AcpPermissionOutcome> RequestAsync(AcpToolCallUpdate toolCall, CancellationToken cancellationToken)
    {
        var sessionId = _sessions.ActiveSessionId;
        if (sessionId is null)
            return Task.FromResult(new AcpPermissionOutcome("cancelled", null));

        long requestId;
        TaskCompletionSource<AcpPermissionOutcome> tcs;
        lock (_pendingGate)
        {
            requestId = _nextRequestId++;
            tcs = new TaskCompletionSource<AcpPermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPermissions[requestId] = tcs;
        }

        var options = new[]
        {
            new AcpPermissionOption("allow-once", "Allow once", "allow_once"),
            new AcpPermissionOption("reject-once", "Reject once", "reject_once")
        };
        _ = _writer.WriteRequestAsync(requestId, AcpMethods.SessionRequestPermission,
            new AcpPermissionRequestParams(sessionId, toolCall, options), cancellationToken);
        return tcs.Task;
    }

    public void RejectAllPending(string reason)
    {
        KeyValuePair<long, TaskCompletionSource<AcpPermissionOutcome>>[] pending;
        lock (_pendingGate)
        {
            pending = [.. _pendingPermissions];
            _pendingPermissions.Clear();
        }
        foreach (var (_, tcs) in pending)
            tcs.TrySetResult(new AcpPermissionOutcome("cancelled", null));
    }

    #endregion

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task RouteAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument? parsed = null;
        try
        {
            parsed = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            await _writer.WriteErrorAsync(null, AcpErrorCodes.ParseError, "Parse error", cancellationToken: cancellationToken);
            return;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            var method = root.TryGetProperty("method", out var methodProp) ? methodProp.GetString() : null;
            var hasId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null;
            object? id = hasId ? ExtractId(idProp) : null;
            JsonElement? paramsValue = root.TryGetProperty("params", out var paramsProp) ? paramsProp : null;

            // Inbound response to an agent→client request (e.g. session/request_permission).
            if (method is null && hasId && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
            {
                RouteRespond(id);
                return;
            }

            if (method is null)
            {
                await _writer.WriteErrorAsync(id, AcpErrorCodes.InvalidRequest, "Invalid request", cancellationToken: cancellationToken);
                return;
            }

            try
            {
                await DispatchAsync(method, id, hasId, paramsValue, cancellationToken);
            }
            catch (AcpRpcException ex)
            {
                if (hasId)
                    await _writer.WriteErrorAsync(id, ex.Code, ex.Message, ex.Data, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Server shutting down; nothing to write.
            }
        }
    }

    private async Task DispatchAsync(string method, object? id, bool hasId, JsonElement? paramsValue, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case AcpMethods.Initialize:
                await InitializeAsync(id, hasId, cancellationToken);
                break;
            case AcpMethods.SessionNew:
                await SessionNewAsync(id, hasId, paramsValue, cancellationToken);
                break;
            case AcpMethods.SessionLoad:
                await SessionLoadAsync(id, hasId, paramsValue, replay: true, cancellationToken);
                break;
            case AcpMethods.SessionResume:
                await SessionResumeAsync(id, hasId, paramsValue, cancellationToken);
                break;
            case AcpMethods.SessionClose:
                await SessionCloseAsync(id, hasId, paramsValue, cancellationToken);
                break;
            case AcpMethods.SessionPrompt:
                await SessionPromptAsync(id, hasId, paramsValue, cancellationToken);
                break;
            case AcpMethods.SessionCancel:
                await SessionCancelAsync(paramsValue, cancellationToken);
                break;
            default:
                if (hasId)
                    await _writer.WriteErrorAsync(id, AcpErrorCodes.MethodNotFound, $"Method not found: {method}", cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task InitializeAsync(object? id, bool hasId, CancellationToken cancellationToken)
    {
        if (!hasId) return; // notification for initialize is ignored
        var version = typeof(AcpServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var result = new AcpInitializeResult(
            ProtocolVersion,
            new AcpAgentCapabilities(
                LoadSession: true,
                PromptCapabilities: new AcpPromptCapabilities(Image: true),
                SessionCapabilities: new AcpSessionCapabilities(Resume: new { }, Close: new { })),
            new AcpAgentInfo(Name: "pisharp", Title: "PiSharp", Version: version),
            []);
        await _writer.WriteResponseAsync(id, result, cancellationToken);
    }

    private async Task SessionNewAsync(object? id, bool hasId, JsonElement? paramsValue, CancellationToken cancellationToken)
    {
        if (!hasId) return;
        if (_sessions.IsTurnActive) throw AcpRpcException.Server("turn_in_progress");
        var info = await _sessions.NewAsync(RequiredParamString(paramsValue, "cwd"), cancellationToken);
        RefreshTranslator(info.SessionId);
        BindCurrentHarness();
        await _writer.WriteResponseAsync(id, new AcpSessionNewResult(info.SessionId), cancellationToken);
    }

    private async Task SessionLoadAsync(object? id, bool hasId, JsonElement? paramsValue, bool replay, CancellationToken cancellationToken)
    {
        if (!hasId) return;
        if (_sessions.IsTurnActive) throw AcpRpcException.Server("turn_in_progress");
        var sessionId = RequiredParamString(paramsValue, "sessionId");
        var cwd = RequiredParamString(paramsValue, "cwd");
        var info = await _sessions.LoadAsync(sessionId, cwd, replay, cancellationToken);
        RefreshTranslator(info.SessionId);
        BindCurrentHarness();

        if (replay)
        {
            var messages = await _sessions.BuildContextAsync(cancellationToken);
            foreach (var update in _translator!.TranslateReplay(messages))
                await _writer.WriteNotificationAsync(AcpMethods.SessionUpdate, new { sessionId = info.SessionId, update }, cancellationToken);
        }

        await _writer.WriteResponseAsync(id, replay ? null : (object)new { }, cancellationToken);
    }

    private async Task SessionResumeAsync(object? id, bool hasId, JsonElement? paramsValue, CancellationToken cancellationToken)
        => await SessionLoadAsync(id, hasId, paramsValue, replay: false, cancellationToken);

    private async Task SessionCloseAsync(object? id, bool hasId, JsonElement? paramsValue, CancellationToken cancellationToken)
    {
        if (!hasId) return;
        var sessionId = RequiredParamString(paramsValue, "sessionId");
        await _sessions.CloseAsync(sessionId, cancellationToken);
        await _writer.WriteResponseAsync(id, null, cancellationToken);
    }

    private async Task SessionPromptAsync(object? id, bool hasId, JsonElement? paramsValue, CancellationToken cancellationToken)
    {
        if (!hasId) return;
        if (_sessions.IsTurnActive) throw AcpRpcException.Server("turn_in_progress");
        var sessionId = RequiredParamString(paramsValue, "sessionId");
        var prompt = AcpContentCodec.Parse(RequiredPrompt(paramsValue));
        var turnTask = RunTurnAsync(id, sessionId, prompt, cancellationToken);
        _turnTasks.Add(turnTask);
        await Task.CompletedTask;
    }

    private async Task SessionCancelAsync(JsonElement? paramsValue, CancellationToken cancellationToken)
    {
        // Notification — never responded to.
        if (TryParamString(paramsValue, "sessionId", out var sessionId) && sessionId is not null)
            _sessions.Cancel(sessionId);
    }

    private async Task RunTurnAsync(object? id, string sessionId, IReadOnlyList<AcpContentBlock> prompt, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _sessions.PromptAsync(sessionId, prompt, cancellationToken);
            if (result?.ErrorMessage is not null)
            {
                await _writer.WriteErrorAsync(id, AcpErrorCodes.ServerError, result.ErrorMessage, cancellationToken: cancellationToken);
                return;
            }
            await _writer.WriteResponseAsync(id, new AcpPromptResult(MapStopReason(result)), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _writer.WriteResponseAsync(id, new AcpPromptResult("cancelled"), cancellationToken);
        }
        catch (AcpRpcException ex)
        {
            await _writer.WriteErrorAsync(id, ex.Code, ex.Message, cancellationToken: cancellationToken);
        }
    }

    private void RouteRespond(object? id)
    {
        if (id is not long requestId) return;
        TaskCompletionSource<AcpPermissionOutcome>? tcs;
        lock (_pendingGate)
        {
            if (!_pendingPermissions.Remove(requestId, out tcs)) return;
        }
        tcs.TrySetResult(new AcpPermissionOutcome("cancelled", null));
    }

    private void RefreshTranslator(string sessionId)
        => _translator = new AcpEventTranslator(sessionId, call => (TitleFor(call.ToolName), AcpEventTranslator.MapToolKind(call.ToolName)));

    private void BindCurrentHarness()
    {
        lock (_rebindGate)
        {
            _subscription?.Dispose();
            _subscription = _sessions.Runtime.Harness.Subscribe(ForwardEventAsync);
        }
    }

    private async Task ForwardEventAsync(AgentHarnessEvent evt, CancellationToken cancellationToken)
    {
        var translator = _translator;
        if (translator is null) return;
        var sessionId = _sessions.ActiveSessionId ?? translator.SessionId;
        foreach (var update in translator.Translate(evt))
            await _writer.WriteNotificationAsync(AcpMethods.SessionUpdate, new { sessionId, update }, cancellationToken);
    }

    private void RegisterMiddleware()
    {
        var gate = AcpPermissionGate.Create(new AcpPermissionGateOptions(
            _mode.ApprovalMode,
            _mode.PermissionAllowlist,
            this,
            ToolMeta));
        _middlewareHandle = _sessions.Runtime.ExtensionManager?.Registry.RegisterMiddleware("acp", gate);
    }

    private (string Title, string Kind) ToolMeta(string toolName)
        => (TitleFor(toolName), AcpEventTranslator.MapToolKind(toolName));

    private string TitleFor(string toolName)
        => _tools.FirstOrDefault(tool => tool.Name == toolName)?.Label ?? toolName;

    private static string MapStopReason(AssistantMessage? message)
    {
        if (message is null) return "end_turn";
        return message.StopReason switch
        {
            "end_turn" => "end_turn",
            "max_tokens" => "max_tokens",
            "aborted" => "cancelled",
            _ => "end_turn"
        };
    }

    private static object? ExtractId(JsonElement idProp) => idProp.ValueKind switch
    {
        JsonValueKind.String => idProp.GetString(),
        JsonValueKind.Number => idProp.TryGetInt64(out var l) ? l : idProp.GetDouble(),
        _ => null
    };

    private static string RequiredParamString(JsonElement? paramsValue, string name)
    {
        if (paramsValue is not JsonElement p || !p.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String || prop.GetString() is not { } value)
            throw AcpRpcException.InvalidParams($"params.{name} is required");
        return value;
    }

    private static bool TryParamString(JsonElement? paramsValue, string name, out string? value)
    {
        value = null;
        if (paramsValue is not JsonElement p || !p.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString();
        return true;
    }

    private static JsonElement RequiredPrompt(JsonElement? paramsValue)
    {
        if (paramsValue is not JsonElement p || !p.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.Array)
            throw AcpRpcException.InvalidParams("params.prompt must be an array of content blocks");
        return prompt;
    }
}
