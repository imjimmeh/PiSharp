using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Serialization;
using PiSharp.Ai;
using PiSharp.Cli.Commands;
using PiSharp.Cli.IO;
using PiSharp.Runtime;
using PiSharp.Runtime.Session;

namespace PiSharp.Cli.Modes;

public static class RpcMode
{
    private static readonly Dictionary<string, TaskCompletionSource<RpcExtensionUiResponseCommand>> PendingExtensionUi = new(StringComparer.Ordinal);

    public static async Task<int> RunAsync(SessionRuntime runtime, IConsoleIO console, CancellationToken cancellationToken = default)
    {
        await using var guard = StdoutGuard.TakeOver(console);
        var writer = new RpcJsonWriter(guard.ProtocolOut);
        var promptTasks = new List<Task>();
        IDisposable? subscription = null;

        void BindCurrentHarness()
        {
            subscription?.Dispose();
            subscription = runtime.Harness.Subscribe((evt, token) => writer.WriteAsync(evt.ToFlat(), token));
        }

        runtime.SetRebindSession((_, _) => { BindCurrentHarness(); return Task.CompletedTask; });
        BindCurrentHarness();

        try
        {
            string? line;
            while ((line = await console.In.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                await HandleLineAsync(runtime, writer, promptTasks, line, cancellationToken);
            }

            await Task.WhenAll(promptTasks);
            return 0;
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    private static async Task HandleLineAsync(SessionRuntime runtime, RpcJsonWriter writer, List<Task> promptTasks, string line, CancellationToken cancellationToken)
    {
        var type = "unknown";
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            type = RequiredString(root, "type");
            id = OptionalString(root, "id");

            switch (type)
            {
                case "prompt":
                    var prompt = AgentJsonSerializer.Deserialize<RpcPromptCommand>(line)!;
                    await StartPromptAsync(runtime, writer, promptTasks, id, prompt.Message, prompt.Images, cancellationToken);
                    break;
                case "steer":
                    runtime.Harness.Steer(AgentMessages.User(RequiredString(root, "message")));
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "follow_up":
                    runtime.Harness.FollowUp(AgentMessages.User(RequiredString(root, "message")));
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "abort":
                    runtime.Harness.Abort();
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "new_session":
                    var newSession = await runtime.NewSessionAsync(cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { cancelled = newSession.Cancelled, reason = newSession.Reason, session = newSession.Session }), cancellationToken);
                    break;
                case "get_state":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, await StateAsync(runtime, cancellationToken)), cancellationToken);
                    break;
                case "set_model":
                    var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(RequiredString(root, "provider"), RequiredString(root, "modelId"), null));
                    await runtime.SetModelAsync(selection, "rpc", cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, selection.Model), cancellationToken);
                    break;
                case "get_available_models":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new RpcAvailableModelsResult(PublicApi.Models.Select(m => m.Descriptor).ToArray())), cancellationToken);
                    break;
                case "set_thinking_level":
                    var level = Enum.Parse<ThinkingLevel>(RequiredString(root, "level"), ignoreCase: true);
                    await runtime.SetThinkingLevelAsync(level, cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "compact":
                    await runtime.Harness.CompactAsync(OptionalString(root, "customInstructions"), cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "fork":
                    var fork = await runtime.ForkAsync(runtime.Session.Metadata, new SessionForkOptions(RequiredString(root, "entryId")), cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { cancelled = fork.Cancelled, reason = fork.Reason, session = fork.Session }), cancellationToken);
                    break;
                case "switch_session":
                    var sessionPath = RequiredString(root, "sessionPath");
                    var session = (await runtime.ListSessionsAsync(null, cancellationToken)).FirstOrDefault(s => s.Path == sessionPath || s.Id == sessionPath)
                        ?? throw new InvalidOperationException($"Session '{sessionPath}' not found.");
                    var switchResult = await runtime.SwitchSessionAsync(session, cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { cancelled = switchResult.Cancelled, reason = switchResult.Reason, session = switchResult.Session }), cancellationToken);
                    break;
                case "get_messages":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new RpcMessagesResult((await runtime.Session.BuildContextAsync(cancellationToken)).Messages)), cancellationToken);
                    break;
                case "get_last_assistant_text":
                    var text = string.Concat((await runtime.Session.BuildContextAsync(cancellationToken)).Messages.OfType<AssistantMessage>().LastOrDefault()?.Content.OfType<TextContent>().Select(content => content.Text) ?? []);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new RpcLastAssistantTextResult(string.IsNullOrEmpty(text) ? null : text)), cancellationToken);
                    break;
                case "extension_ui_response":
                    var response = AgentJsonSerializer.Deserialize<RpcExtensionUiResponseCommand>(line)!;
                    if (PendingExtensionUi.Remove(response.RequestId, out var pending)) pending.TrySetResult(response);
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "set_session_name":
                    await runtime.Harness.SetSessionNameAsync(RequiredString(root, "name"), cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "cycle_model":
                    var nextSelection = RuntimeModelSelector.Cycle(runtime.CurrentModelSelection, +1);
                    await runtime.SetModelAsync(nextSelection, "rpc", cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, runtime.Harness.Model), cancellationToken);
                    break;
                case "cycle_thinking_level":
                    var nextThinking = RuntimeModelSelector.CycleThinking(runtime.Harness.Model, runtime.Harness.ThinkingLevel, +1);
                    await runtime.SetThinkingLevelAsync(nextThinking, cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { level = nextThinking.ToString().ToLowerInvariant() }), cancellationToken);
                    break;
                case "get_commands":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, BuildCommandRegistry(runtime).Commands.Where(command => command.SourceId != "builtin").Select(ToRpcCommandInfo)), cancellationToken);
                    break;
                case "run_command":
                    var commandText = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : RequiredString(root, "command");
                    var result = await BuildCommandRegistry(runtime).ExecuteAsync(commandText, CreateSlashCommandContext(runtime, writer), cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, result), cancellationToken);
                    break;
                case "clone":
                    var clone = await runtime.ForkAsync(runtime.Session.Metadata, new SessionForkOptions(null), cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { cancelled = clone.Cancelled, reason = clone.Reason, session = clone.Session }), cancellationToken);
                    break;
                case "get_session_stats":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { messages = (await runtime.Session.BuildContextAsync(cancellationToken)).Messages.Count }), cancellationToken);
                    break;
                case "get_fork_messages":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { entries = await runtime.GetForkableEntriesAsync(cancellationToken) }), cancellationToken);
                    break;
                case "set_auto_compaction":
                    runtime.AutoCompactionEnabled = RequiredBool(root, "enabled");
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { autoCompactionEnabled = runtime.AutoCompactionEnabled }), cancellationToken);
                    break;
                case "set_auto_retry":
                    runtime.AutoRetryEnabled = RequiredBool(root, "enabled");
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { autoRetryEnabled = runtime.AutoRetryEnabled }), cancellationToken);
                    break;
                case "abort_retry":
                    runtime.ResetRetryState();
                    await writer.WriteAsync(RpcResponse.Ok(id, type), cancellationToken);
                    break;
                case "set_steering_mode":
                    var steeringMode = RequiredString(root, "mode");
                    if (steeringMode is not ("all" or "one-at-a-time")) throw new InvalidOperationException($"Invalid steering mode '{steeringMode}'. Must be 'all' or 'one-at-a-time'.");
                    runtime.SteeringMode = steeringMode;
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { steeringMode }), cancellationToken);
                    break;
                case "set_follow_up_mode":
                    var followUpMode = RequiredString(root, "mode");
                    if (followUpMode is not ("all" or "one-at-a-time")) throw new InvalidOperationException($"Invalid follow-up mode '{followUpMode}'. Must be 'all' or 'one-at-a-time'.");
                    runtime.FollowUpMode = followUpMode;
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { followUpMode }), cancellationToken);
                    break;
                case "export_html":
                    var outputPath = OptionalString(root, "outputPath") ?? Path.Combine(Path.GetTempPath(), $"pisharp-export-{Guid.NewGuid():N}.html");
                    var resolvedPath = Path.GetFullPath(outputPath);
                    var allowedRoot = Path.GetFullPath(Path.GetTempPath());
                    if (!resolvedPath.StartsWith(allowedRoot + Path.DirectorySeparatorChar) && resolvedPath != allowedRoot)
                        throw new InvalidOperationException($"Output path '{resolvedPath}' must be under the temp directory.");
                    await HtmlSessionRenderer.ExportToFileAsync(runtime.Session, resolvedPath, cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { outputPath = resolvedPath }), cancellationToken);
                    break;
                case "bash":
                    var bashCommand = RequiredString(root, "command");
                    var excludeFromContext = root.TryGetProperty("excludeFromContext", out var excl) && excl.GetBoolean();
                    var bashResult = await runtime.DispatchUserBashAsync(bashCommand, excludeFromContext, cancellationToken);
                    await writer.WriteAsync(RpcResponse.Ok(id, type, BuildUserBashRpcData(bashResult)), cancellationToken);
                    break;
                case "abort_bash":
                    await writer.WriteAsync(RpcResponse.Ok(id, type, new { message = "No active bash operation to abort." }), cancellationToken);
                    break;
                default:
                    await writer.WriteAsync(RpcResponse.Fail(id, type, $"Unknown RPC command '{type}'."), cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await writer.WriteAsync(RpcResponse.Fail(id, type, ex.Message), cancellationToken);
        }
    }

    private static SlashCommandRegistry BuildCommandRegistry(SessionRuntime runtime)
        => SlashCommandRegistryFactory.Create(runtime);

    private static object ToRpcCommandInfo(SlashCommandDefinition command)
    {
        var source = command.SourceId switch
        {
            "skill" => "skill",
            "prompt-template" => "prompt",
            _ => "extension"
        };
        return new
        {
            command.Name,
            command.Description,
            Source = source,
            SourceInfo = new
            {
                Path = command.SourceId,
                Source = command.SourceId,
                Scope = "temporary",
                Origin = "top-level"
            }
        };
    }

    private static SlashCommandContext CreateSlashCommandContext(SessionRuntime runtime, RpcJsonWriter writer)
        => new(
            string.Empty,
            runtime,
            (_, options, _) => Task.FromResult<string?>(options.FirstOrDefault()),
            (_, _) => Task.FromResult<string?>(null),
            async (message, token) => await writer.WriteAsync(new { type = "notification", message }, token));

    private static object? BuildUserBashRpcData(SessionRuntime.RuntimeUserBashHookResult? result)
    {
        if (result is null) return null;
        if (result.Result is { } bashResult)
        {
            return new { command = bashResult.Command, exitCode = bashResult.ExitCode, output = bashResult.Output, error = bashResult.Error };
        }

        if (result.Operations is { } operations)
        {
            return new
            {
                operations = new
                {
                    command = operations.Command,
                    cwd = operations.Cwd,
                    timeout = operations.Timeout,
                    excludeFromContext = operations.ExcludeFromContext
                }
            };
        }

        return null;
    }

    private static async Task StartPromptAsync(SessionRuntime runtime, RpcJsonWriter writer, List<Task> promptTasks, string? id, string message, IReadOnlyList<ImageContent>? images, CancellationToken cancellationToken)
    {
        if (runtime.Harness.Phase != AgentHarnessPhase.Idle)
        {
            await writer.WriteAsync(RpcResponse.Fail(id, "prompt", "Harness is busy."), cancellationToken);
            return;
        }

        await writer.WriteAsync(RpcResponse.Ok(id, "prompt"), cancellationToken);
        promptTasks.Add(Task.Run(async () =>
        {
            try { await runtime.SubmitPromptAsync(message, images, "rpc", cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await writer.WriteAsync(RpcResponse.Fail(id, "prompt", ex.Message), CancellationToken.None); }
        }, CancellationToken.None));
    }

    private static async Task<RpcSessionState> StateAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        var context = await runtime.Session.BuildContextAsync(cancellationToken);
        return new RpcSessionState(
            runtime.Harness.Model,
            runtime.Harness.ThinkingLevel,
            runtime.Harness.Phase == AgentHarnessPhase.Turn,
            runtime.Harness.Phase == AgentHarnessPhase.Compaction,
            runtime.SteeringMode,
            runtime.FollowUpMode,
            runtime.Session.Metadata.Path,
            runtime.Session.Metadata.Id,
            await runtime.Session.GetSessionNameAsync(cancellationToken),
            false,
            context.Messages.Count,
            0);
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"Missing required RPC field '{property}'.");
        return value.GetString()!;
    }

    private static bool RequiredBool(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidOperationException($"Missing required RPC field '{property}'.");
        return value.GetBoolean();
    }

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"RPC field '{property}' must be a string.");
        return value.GetString();
    }

    private sealed class RpcJsonWriter(TextWriter writer)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteAsync(object value, CancellationToken cancellationToken = default)
        {
            var json = AgentJsonSerializer.Serialize(value);
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
}
