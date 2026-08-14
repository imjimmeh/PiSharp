using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using PiSharp.Tools.Shared;

namespace PiSharp.DeclarativeTools;

/// <summary>
/// Result details for a script tool run, mirroring <c>BashToolDetails</c>
/// (plan §5.3). Null when neither truncation nor temp-file spill occurred.
/// </summary>
public sealed record ScriptToolDetails(TruncationResult? Truncation = null, string? FullOutputPath = null);

/// <summary>
/// Builds the <see cref="ExtensionToolRegistration.ExecuteAsync"/> delegate for script tools
/// (plan §5.3, §7). Execution reuses the <see cref="IExecutionEnv"/> shell with
/// <c>OutputAccumulator</c> capture, 100 ms-throttled streaming updates, and
/// BashTool-compatible timeout/exit-code semantics.
/// </summary>
public static class ScriptToolExecutor
{
    private const int UpdateThrottleMilliseconds = 100;
    private const string ArgsTempFilePrefix = "pi-tool-args-";
    private const string ArgsTempFileSuffix = ".json";

    /// <summary>
    /// Creates the execute delegate for <paramref name="tool"/>. The interpreter is chosen
    /// from the script extension and the current OS; a missing interpreter surfaces as a
    /// spawn error at execution time.
    /// </summary>
    public static ExtensionToolExecuteAsync Create(IExecutionEnv env, ToolDefinition tool, TimeSpan? defaultTimeout)
    {
        if (tool.ScriptPath is null)
            throw new ArgumentException("Tool has no script path.", nameof(tool));

        var interpreter = ResolveInterpreter(tool.ScriptPath);
        var timeout = tool.Timeout ?? defaultTimeout;

        return async (toolCallId, parameters, cancellationToken, onUpdate) =>
        {
            var argsJson = ToCompactJson(parameters);
            var argsFilePath = await WriteArgsFileAsync(env, argsJson, cancellationToken);
            try
            {
                return await ExecuteAsync(env, tool, interpreter, argsJson, argsFilePath, timeout, toolCallId, onUpdate, cancellationToken);
            }
            finally
            {
                await TryDeleteAsync(env, argsFilePath, cancellationToken);
            }
        };
    }

    private static string ToCompactJson(JsonElement parameters)
    {
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "{}";
        // Re-encode so the bytes piped to the script are always compact.
        return JsonSerializer.Serialize(parameters);
    }

    private static async Task<string> WriteArgsFileAsync(IExecutionEnv env, string argsJson, CancellationToken cancellationToken)
    {
        var created = await env.CreateTempFileAsync(ArgsTempFilePrefix, ArgsTempFileSuffix, cancellationToken);
        if (created.IsErr) throw new InvalidOperationException($"Failed to create arguments file: {created.Error.Message}");
        var path = created.Value;
        var written = await env.WriteFileAsync(path, argsJson, cancellationToken);
        if (written.IsErr) throw new InvalidOperationException($"Failed to write arguments file: {written.Error.Message}");
        return path;
    }

    private static async Task<AgentToolResult<object?>> ExecuteAsync(
        IExecutionEnv env,
        ToolDefinition tool,
        string interpreter,
        string argsJson,
        string argsFilePath,
        TimeSpan? timeout,
        string toolCallId,
        AgentToolUpdateCallback<object?>? onUpdate,
        CancellationToken cancellationToken)
    {
        var command = $"{interpreter} \"{tool.ScriptPath}\" < \"{argsFilePath}\"";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PISHARP_TOOL_NAME"] = tool.Name,
            ["PISHARP_TOOL_CWD"] = env.Cwd,
            ["PISHARP_TOOL_ARGS"] = argsJson
        };

        var output = new OutputAccumulator(env, new OutputAccumulatorOptions(TempFilePrefix: "pi-tool"));
        var receivedByteOutput = false;
        var lastUpdateAt = DateTimeOffset.MinValue;

        onUpdate?.Invoke(new AgentToolResult<object?>([], null));

        async ValueTask HandleBytes(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            receivedByteOutput = true;
            await output.AppendAsync(data, token).ConfigureAwait(false);
            if (onUpdate is null) return;
            var now = DateTimeOffset.UtcNow;
            if (now - lastUpdateAt < TimeSpan.FromMilliseconds(UpdateThrottleMilliseconds)) return;
            lastUpdateAt = now;
            var snapshot = await output.SnapshotAsync(persistIfTruncated: true, token).ConfigureAwait(false);
            onUpdate(new AgentToolResult<object?>([new TextContent(snapshot.Content)], ToDetails(snapshot)));
        }

        var execOptions = new ExecutionOptions(
            Cwd: env.Cwd,
            Environment: environment,
            Timeout: timeout,
            OnOutputBytes: HandleBytes);

        var shellResult = await env.ExecAsync(command, execOptions, cancellationToken).ConfigureAwait(false);
        if (shellResult.IsOk && !receivedByteOutput)
        {
            var combined = shellResult.Value.Stdout + shellResult.Value.Stderr;
            if (combined.Length > 0) await output.AppendAsync(Encoding.UTF8.GetBytes(combined), cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await FinishOutputAsync(output, cancellationToken).ConfigureAwait(false);

        if (shellResult.IsErr)
        {
            var status = shellResult.Error.Code switch
            {
                ExecutionErrorCode.Aborted => "Command aborted",
                ExecutionErrorCode.Timeout => timeout is not null ? $"Command timed out after {timeout.Value.TotalSeconds:g} seconds" : "Command timed out",
                _ => shellResult.Error.Message
            };
            throw new InvalidOperationException(AppendStatus(FormatOutput(snapshot).Text, status), shellResult.Error);
        }

        if (shellResult.Value.ExitCode != 0)
        {
            var status = $"Command exited with code {shellResult.Value.ExitCode}";
            if (tool.AllowNonZeroExit)
            {
                var (text, details) = FormatOutput(snapshot);
                return new AgentToolResult<object?>([new TextContent(AppendStatus(text, status))], details);
            }
            throw new InvalidOperationException(AppendStatus(FormatOutput(snapshot).Text, status));
        }

        var (resultText, resultDetails) = FormatOutput(snapshot);
        return new AgentToolResult<object?>([new TextContent(resultText)], resultDetails);
    }

    private static async Task<OutputSnapshot> FinishOutputAsync(OutputAccumulator output, CancellationToken cancellationToken)
    {
        await output.FinishAsync(cancellationToken).ConfigureAwait(false);
        return await output.SnapshotAsync(persistIfTruncated: true, cancellationToken).ConfigureAwait(false);
    }

    private static (string Text, ScriptToolDetails? Details) FormatOutput(OutputSnapshot snapshot)
    {
        var text = string.IsNullOrEmpty(snapshot.Content) ? "(no output)" : snapshot.Content;
        var details = ToDetails(snapshot);
        if (snapshot.Truncation.Truncated)
        {
            var startLine = snapshot.Truncation.TotalLines - snapshot.Truncation.OutputLines + 1;
            var endLine = snapshot.Truncation.TotalLines;
            if (snapshot.Truncation.LastLinePartial)
            {
                text += $"\n\n[Showing last {Truncation.FormatSize(snapshot.Truncation.OutputBytes)} of line {endLine} (line is {Truncation.FormatSize(snapshot.Truncation.TotalBytes)}). Full output: {snapshot.FullOutputPath}]";
            }
            else if (snapshot.Truncation.TruncatedBy == "lines")
            {
                text += $"\n\n[Showing lines {startLine}-{endLine} of {snapshot.Truncation.TotalLines}. Full output: {snapshot.FullOutputPath}]";
            }
            else
            {
                text += $"\n\n[Showing lines {startLine}-{endLine} of {snapshot.Truncation.TotalLines} ({Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit). Full output: {snapshot.FullOutputPath}]";
            }
        }
        return (text, details);
    }

    private static ScriptToolDetails? ToDetails(OutputSnapshot snapshot)
        => snapshot.Truncation.Truncated || snapshot.FullOutputPath is not null
            ? new ScriptToolDetails(snapshot.Truncation.Truncated ? snapshot.Truncation : null, snapshot.FullOutputPath)
            : null;

    private static string AppendStatus(string text, string status)
        => string.IsNullOrEmpty(text) ? status : $"{text}\n\n{status}";

    private static async Task TryDeleteAsync(IExecutionEnv env, string path, CancellationToken cancellationToken)
    {
        var result = await env.RemoveAsync(path, recursive: false, force: true, cancellationToken);
        _ = result; // best-effort cleanup
    }

    private static string ResolveInterpreter(string scriptPath)
        => Path.GetExtension(scriptPath).ToLowerInvariant() switch
        {
            ".sh" or ".bash" => "bash",
            ".py" => OperatingSystem.IsWindows() ? "py" : "python3",
            ".ts" => "node",
            var extension => throw new ArgumentException($"Unsupported script extension '{extension}'.")
        };
}
