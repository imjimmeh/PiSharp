using System.Text.Json;
using PiSharp.Cli.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Cli.IO;
using PiSharp.Runtime;

namespace PiSharp.Cli.Modes;

/// <summary>
/// Minimal in-process ACP (Agent Client Protocol) mode: a hand-rolled, newline-delimited
/// JSON-RPC 2.0 server over stdio (the canonical ACP transport — the client spawns the agent).
/// Implements the v1 client→agent surface: initialize, session/new, session/prompt (text),
/// session/close, session/cancel, plus standard JSON-RPC error codes.
/// </summary>
public static class AcpMode
{
    public const int ProtocolVersion = 1;

    public static async Task<int> RunAsync(SessionRuntime runtime, IConsoleIO console, AcpApprovalMode approvalMode, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(nameof(AcpMode));
        logger.LogInformation("ACP mode started approvalMode={ApprovalMode}", approvalMode);
        await using var guard = StdoutGuard.TakeOver(console);
        var writer = new AcpJsonWriter(guard.ProtocolOut);

        // v1 has one active in-process session: sessionId maps to the runtime's current session.
        var sessionIdPrefix = "sess_";
        string? activeSessionId = null;
        var turnActive = false;

        try
        {
            string? line;
            while ((line = await console.In.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                await HandleLineAsync(runtime, writer, line, sessionIdPrefix, () => activeSessionId, id => activeSessionId = id, () => turnActive, v => turnActive = v, logger, cancellationToken);
            }

            return 0;
        }
        finally
        {
            runtime.Harness.Abort();
        }
    }

    internal static async Task HandleLineAsync(
        SessionRuntime runtime,
        AcpJsonWriter writer,
        string line,
        string sessionIdPrefix,
        Func<string?> getActiveSession,
        Action<string?> setActiveSession,
        Func<bool> getTurnActive,
        Action<bool> setTurnActive,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            await writer.WriteErrorAsync(null, -32700, "Parse error");
            return;
        }

        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        var id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null ? (object?)idProp : null;
        var isNotification = !root.TryGetProperty("id", out _);
        JsonElement? paramsValue = root.TryGetProperty("params", out var p) ? p : null;
        logger.LogDebug("ACP request received method={Method} id={Id}", method, id);

        if (method is null)
        {
            await writer.WriteErrorAsync(id, -32600, "Invalid request");
            return;
        }

        switch (method)
        {
            case "initialize":
                await writer.WriteAsync(id, new
                {
                    protocolVersion = ProtocolVersion,
                    agentCapabilities = new
                    {
                        loadSession = true,
                        promptCapabilities = new { image = false },
                        sessionCapabilities = new { resume = new { }, close = new { } }
                    },
                    agentInfo = new { name = "pisharp", title = "PiSharp", version = VersionInfo.Current },
                    authMethods = Array.Empty<object>()
                });
                break;

            case "session/new":
            {
                logger.LogDebug("ACP session/new requested");
                if (getActiveSession() is not null && getTurnActive())
                {
                    await writer.WriteErrorAsync(id, -32000, "turn_in_progress");
                    break;
                }

                if (!TryGetParamString(paramsValue, "cwd", out var cwd) || !string.Equals(Path.GetFullPath(cwd ?? string.Empty), runtime.Session.Metadata.Cwd, StringComparison.Ordinal))
                {
                    await writer.WriteErrorAsync(id, -32602, "cwd must equal the process cwd (in-process ACP limitation)");
                    break;
                }

                var created = await runtime.NewSessionAsync(cancellationToken);
                setActiveSession(sessionIdPrefix + (created.Session?.Id ?? "unknown"));
                setTurnActive(false);
                await writer.WriteAsync(id, new { sessionId = getActiveSession() });
                break;
            }

            case "session/prompt":
            {
                var sessionId = getActiveSession();
                if (sessionId is null)
                {
                    await writer.WriteErrorAsync(id, -32000, "no active session");
                    break;
                }

                if (getTurnActive())
                {
                    await writer.WriteErrorAsync(id, -32000, "turn_in_progress");
                    break;
                }

                if (paramsValue is not JsonElement pElem || !pElem.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.Array)
                {
                    await writer.WriteErrorAsync(id, -32602, "params.prompt is required");
                    break;
                }

                var text = ExtractPromptText(prompt);
                logger.LogDebug("ACP session/prompt submitted length={Length}", text.Length);
                setTurnActive(true);
                try
                {
                    var result = await runtime.SubmitPromptAsync(text, null, "acp", cancellationToken);
                    var stopReason = MapStopReason(result);
                    setTurnActive(false);
                    logger.LogDebug("ACP session/prompt completed stopReason={StopReason}", stopReason);
                    await writer.WriteAsync(id, new { stopReason });
                }
                catch (OperationCanceledException)
                {
                    setTurnActive(false);
                    logger.LogDebug("ACP session/prompt cancelled");
                    await writer.WriteAsync(id, new { stopReason = "cancelled" });
                }
                catch (Exception exception)
                {
                    setTurnActive(false);
                    logger.LogError(exception, "ACP session/prompt failed");
                    throw;
                }
                break;
            }
            case "session/close":
                runtime.Harness.Abort();
                setActiveSession(null);
                setTurnActive(false);
                if (!isNotification) await writer.WriteAsync(id, null);
                break;

            case "session/cancel":
                runtime.Harness.Abort();
                setTurnActive(false);
                break;

            default:
                await writer.WriteErrorAsync(id, -32601, $"Method not found: {method}");
                break;
        }
    }

    private static string ExtractPromptText(JsonElement promptArray)
    {
        var parts = new List<string>();
        foreach (var block in promptArray.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }

        return string.Join("\n", parts);
    }

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

    private static bool TryGetParamString(JsonElement? paramsValue, string name, out string? value)
    {
        value = null;
        if (paramsValue is not JsonElement p || !p.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString();
        return true;
    }
}

/// <summary>Single-writer JSON-RPC response gate over a TextWriter.</summary>
public sealed class AcpJsonWriter(TextWriter writer)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task WriteAsync(object? id, object? result, CancellationToken cancellationToken = default)
        => WriteJsonAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);

    public Task WriteErrorAsync(object? id, int code, string message, CancellationToken cancellationToken = default)
        => WriteJsonAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, cancellationToken);

    private async Task WriteJsonAsync(object obj, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
