using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Server.Hosting;

using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Ai;
using PiSharp.Agent.Core.Events;
using PiSharp.Runtime;
using PiSharp.Server.Authentication;
using PiSharp.Continuity.Contracts;
using PiSharp.Server.Contracts;
using PiSharp.Extensions;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.UiBridge;

namespace PiSharp.Server.WebSockets;

public sealed class PiServerWebSocketHandler(
    ServerSessionRegistry registry,
    ApiKeyValidator apiKeys,
    ILogger<PiServerWebSocketHandler> logger,
    IServerUiBridge? uiBridge = null,
    PiServerCommandDelegates? delegates = null,
    ThemeRegistry? themeRegistry = null,
    TelemetryMetricsAggregator? metrics = null,
    PiServerHostOptions? options = null)
{
    private readonly int _maxMessageBytes = options?.MaxMessageBytes ?? 8 * 1024 * 1024;
    private readonly int _maxConcurrentCommands = options?.MaxConcurrentCommands ?? 4;
    private IServerUiBridge Bridge => uiBridge ?? NoOpServerUiBridge.Instance;
    private ThemeRegistry Themes => themeRegistry ?? new ThemeRegistry();

    private sealed class NoOpServerUiBridge : IServerUiBridge
    {
        public static NoOpServerUiBridge Instance { get; } = new();
        public Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, CancellationToken cancellationToken = default)
            => Task.FromResult(new ServerUiResponse(intent.RequestId, null, Cancelled: true));
        public void ResolveUiAsync(string requestId, string? value, bool cancelled) { }
    }

    public async Task HandleHttpAsync(HttpContext context)
    {
        if (!apiKeys.Validate(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid API key.");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected WebSocket request.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await RunSocketAsync(socket, context.RequestAborted);
    }

    public async Task RunSocketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var sendGate = new SemaphoreSlim(1, 1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventPumps = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        var pumpGuard = new object();
        var dispatchChannel = Channel.CreateUnbounded<string>();

        async Task SendAsync(object payload, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ServerJsonSerializer.Options);
            await sendGate.WaitAsync(token);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
            finally { sendGate.Release(); }
        }

        Task EnsureEventPumpAsync(LiveServerSession live, long sinceSequence = 0)
        {
            if (eventPumps.ContainsKey(live.Id)) return Task.CompletedTask;
            lock (pumpGuard)
            {
                // Double-checked so concurrent attaches to the same session start exactly one pump.
                if (eventPumps.ContainsKey(live.Id)) return Task.CompletedTask;
                Task pump = null!;
                pump = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var evt in live.ReadEventsAsync(sinceSequence, linked.Token)) await SendAsync(evt, linked.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { logger.LogWarning(ex, "Event pump failed for {ServerSessionId}", live.Id); }
                    finally
                    {
                        // Remove only the exact instance this pump registered, so a finished pump
                        // can be restarted by a later attach instead of blocking with a stale entry.
                        ((ICollection<KeyValuePair<string, Task>>)eventPumps).Remove(new KeyValuePair<string, Task>(live.Id, pump));
                    }
                }, CancellationToken.None);
                eventPumps[live.Id] = pump;
                return Task.CompletedTask;
            }
        }

        // Bounded dispatch: at most MaxConcurrentCommands commands run in parallel per connection;
        // the rest queue on the channel until an in-flight dispatch completes.
        var dispatchTask = Parallel.ForEachAsync(
            dispatchChannel.Reader.ReadAllAsync(linked.Token),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentCommands, CancellationToken = linked.Token },
            async (message, _) =>
            {
                var responseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    var response = await DispatchTextCommandAsync(message, EnsureEventPumpAsync, SendAsync, responseSent.Task, linked.Token);
                    try
                    {
                        await SendAsync(response, linked.Token);
                        logger.LogDebug("Command response sent for {CommandType}", response.Command);
                    }
                    finally
                    {
                        responseSent.TrySetResult();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { logger.LogWarning(ex, "Command dispatch failed"); }
            });

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, cancellationToken);
                if (message is null) break;
                await dispatchChannel.Writer.WriteAsync(message);
            }
        }
        finally
        {
            linked.Cancel();
            dispatchChannel.Writer.TryComplete();
            try { await dispatchTask; }
            catch (OperationCanceledException) { }
            catch { }
            try { await Task.WhenAll(eventPumps.Values); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
        }
    }

    public async Task<ServerResponse> DispatchTextCommandAsync(string json, Func<LiveServerSession, long, Task>? ensureEventPump = null, Func<object, CancellationToken, Task>? sendAsync = null, Task? responseSent = null, CancellationToken cancellationToken = default)
    {
        ServerCommandEnvelope? envelope = null;
        try
        {
            envelope = JsonSerializer.Deserialize<ServerCommandEnvelope>(json, ServerJsonSerializer.Options)
                ?? throw new InvalidOperationException("Invalid command envelope.");
            if (string.IsNullOrWhiteSpace(envelope.Type)) return ServerResponse.Fail(envelope.Id, "unknown", "invalid_command", "Command type is required.");
            logger.LogDebug("Dispatching command {CommandType}", envelope.Type);
            return envelope.Type switch
            {
                ServerCommandTypes.CreateSession => await CreateSessionAsync(json, envelope, ensureEventPump, cancellationToken),
                ServerCommandTypes.Attach => await AttachAsync(json, envelope, ensureEventPump, cancellationToken),
                ServerCommandTypes.DisposeSession => await DisposeSessionAsync(envelope, cancellationToken),
                ServerCommandTypes.Prompt => await PromptAsync(json, envelope, sendAsync, responseSent ?? Task.CompletedTask, cancellationToken),
                ServerCommandTypes.Steer => await TextQueueAsync(json, envelope, ServerCommandTypes.Steer, cancellationToken),
                ServerCommandTypes.FollowUp => await TextQueueAsync(json, envelope, ServerCommandTypes.FollowUp, cancellationToken),
                ServerCommandTypes.QueueNextTurn => await TextQueueAsync(json, envelope, ServerCommandTypes.QueueNextTurn, cancellationToken),
                ServerCommandTypes.Abort => Abort(envelope),
                ServerCommandTypes.GetState => await GetStateAsync(envelope, cancellationToken),
                ServerCommandTypes.GetMessages => await GetMessagesAsync(envelope, cancellationToken),
                ServerCommandTypes.ListSessions => await ListSessionsAsync(json, envelope, cancellationToken),
                ServerCommandTypes.SetModel => await SetModelAsync(json, envelope, cancellationToken),
                ServerCommandTypes.SetThinkingLevel => await SetThinkingLevelAsync(json, envelope, cancellationToken),
                ServerCommandTypes.Compact => await CompactAsync(json, envelope, sendAsync, responseSent ?? Task.CompletedTask, cancellationToken),
                ServerCommandTypes.NewSession => await NewSessionAsync(envelope, cancellationToken),
                ServerCommandTypes.SwitchSession => await SwitchSessionAsync(json, envelope, cancellationToken),
                ServerCommandTypes.Fork => await ForkAsync(json, envelope, cancellationToken),
                ServerCommandTypes.SetSessionName => await SetSessionNameAsync(json, envelope, cancellationToken),
                ServerCommandTypes.RunCommand => await RunCommandAsync(json, envelope, cancellationToken),
                ServerCommandTypes.CompleteCommand => await CompleteCommandAsync(json, envelope, cancellationToken),
                ServerCommandTypes.ProcessInput => await ProcessInputAsync(json, envelope, cancellationToken),
                ServerCommandTypes.UiResponse => await UiResponseAsync(json, envelope),
                ServerCommandTypes.GetTheme => await GetThemeAsync(envelope),
                ServerCommandTypes.GetSessionSnapshot => await GetSessionSnapshotAsync(envelope, cancellationToken),
                ServerCommandTypes.GetForkMessages => await GetForkMessagesAsync(envelope, cancellationToken),
                ServerCommandTypes.GetExtensionLoadStatus => await GetExtensionLoadStatusAsync(envelope),
                ServerCommandTypes.GetExtensionShortcuts => await GetExtensionShortcutsAsync(envelope),
                ServerCommandTypes.GetExtensionRegistry => await GetExtensionRegistryAsync(envelope),
                ServerCommandTypes.ResolveTool => await ResolveToolAsync(json, envelope),
                ServerCommandTypes.CycleThinkingLevel => await CycleThinkingLevelAsync(envelope, cancellationToken),
                ServerCommandTypes.GetAvailableModels => GetAvailableModels(envelope),
                ServerCommandTypes.GetCommands => GetCommandsNotAvailable(envelope),
                ServerCommandTypes.GetLastAssistantText => await GetLastAssistantTextAsync(envelope, cancellationToken),
                ServerCommandTypes.GetStartupMessages => await GetStartupMessagesAsync(envelope, cancellationToken),
                ServerCommandTypes.PostStartupChecks => await PostStartupChecksAsync(envelope, cancellationToken),
                ServerCommandTypes.McpStatus => await GetMcpStatusAsync(envelope, cancellationToken),
                ServerCommandTypes.InstallExtension => await InstallExtensionAsync(json, envelope, cancellationToken),
                ServerCommandTypes.UpdateExtension => await UpdateExtensionAsync(json, envelope, cancellationToken),
                ServerCommandTypes.RemoveExtension => await RemoveExtensionAsync(json, envelope, cancellationToken),
                ServerCommandTypes.ListInstalledExtensions => await ListInstalledExtensionsAsync(envelope, cancellationToken),
                ServerCommandTypes.ManageSkill => await ManageSkillAsync(json, envelope, cancellationToken),
                ServerCommandTypes.GetSkills => await GetSkillsAsync(envelope, cancellationToken),
                ServerCommandTypes.ListThemes => await ListThemesAsync(envelope),
                ServerCommandTypes.SetTheme => await SetThemeAsync(json, envelope, cancellationToken),
                ServerCommandTypes.SetPlanMode => await SetPlanModeAsync(json, envelope, cancellationToken),
                ServerCommandTypes.GetPlanMode => await GetPlanModeAsync(envelope, cancellationToken),
                ServerCommandTypes.GetMetrics => await GetMetricsAsync(envelope),
                ServerCommandTypes.GetSessionStats => await GetSessionStatsAsync(envelope, cancellationToken),
                // --- P23: continuity daemon wire surface (plan C5 §4.9) ---
                ServerCommandTypes.SetGoal => await SetGoalAsync(json, envelope, cancellationToken),
                ServerCommandTypes.GetGoal => await GetGoalAsync(json, envelope, cancellationToken),
                ServerCommandTypes.ScheduleJob => await ScheduleJobAsync(json, envelope, cancellationToken),
                ServerCommandTypes.ListJobs => await ListJobsAsync(json, envelope, cancellationToken),
                ServerCommandTypes.CancelJob => await CancelJobAsync(json, envelope, cancellationToken),
                ServerCommandTypes.Autonomous => await AutonomousAsync(json, envelope, cancellationToken),
                ServerCommandTypes.GetContinuityState => await GetContinuityStateAsync(json, envelope, cancellationToken),
                ServerCommandTypes.Shutdown => await ShutdownAsync(json, envelope, responseSent ?? Task.CompletedTask),
                _ => ServerResponse.Fail(envelope.Id, envelope.Type, "unknown_command", $"Unknown command '{envelope.Type}'.")
            };
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Invalid command JSON");
            return ServerResponse.Fail(envelope?.Id, envelope?.Type ?? "unknown", "invalid_json", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Command {CommandType} failed", envelope?.Type ?? "unknown");
            return ServerResponse.Fail(envelope?.Id, envelope?.Type ?? "unknown", "command_failed", ex.Message);
        }
    }

    private async Task<ServerResponse> CreateSessionAsync(string json, ServerCommandEnvelope envelope, Func<LiveServerSession, long, Task>? ensureEventPump, CancellationToken cancellationToken)
    {
        var request = RequirePayload<CreateServerSessionRequest>(json);
        var startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        logger.LogDebug("Creating server session");
        var created = await registry.CreateAsync(request, cancellationToken);
        var elapsedMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        logger.LogDebug("Created server session in {ElapsedMilliseconds:0.0} ms", elapsedMilliseconds);
        if (registry.TryGet(created.ServerSessionId, out var live))
        {
            // Feed the daemon theme registry from this session's theme resources so list_themes/
            // get_theme can answer before any explicit set_theme. Discovery is not a precondition
            // for the client session response.
            logger.LogDebug("Queueing server session theme merge");
            _ = MergeSessionThemesAsync(Themes, live.Runtime.Resources?.ThemePaths?.ToArray());
            logger.LogDebug("Starting server session event pump");
            if (ensureEventPump is not null) await ensureEventPump(live, 0);
            logger.LogDebug("Started server session event pump");
        }

        return ServerResponse.Ok(envelope.Id, envelope.Type, created);
    }

    private async Task MergeSessionThemesAsync(ThemeRegistry themes, IReadOnlyList<string>? themePaths)
    {
        try
        {
            logger.LogDebug("Merging server session themes");
            await themes.MergeAsync(themePaths, CancellationToken.None);
            logger.LogDebug("Merged server session themes");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Server session theme merge failed");
        }
    }

    private async Task<ServerResponse> AttachAsync(string json, ServerCommandEnvelope envelope, Func<LiveServerSession, long, Task>? ensureEventPump, CancellationToken cancellationToken)
    {
        var command = RequirePayload<AttachCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        var replay = live.EventLog.ReplayFrom(command.SinceSequence);
        if (ensureEventPump is not null) await ensureEventPump(live, command.SinceSequence);
        return ServerResponse.Ok(envelope.Id, envelope.Type, new AttachResult(live.Id, replay.FromSequence, replay.HeadSequence, replay.Gap, replay.Events.Count));
    }

    private async Task<ServerResponse> DisposeSessionAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
        => ServerResponse.Ok(envelope.Id, envelope.Type, new { disposed = await registry.DisposeAsync(RequiredSessionId(envelope)) });

    private Task<ServerResponse> PromptAsync(string json, ServerCommandEnvelope envelope, Func<object, CancellationToken, Task>? sendAsync, Task responseSent, CancellationToken cancellationToken)
    {
        var command = RequirePayload<PromptCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        _ = Task.Run(async () =>
        {
            try
            {
                await responseSent;
                await live.RunExclusiveAsync((runtime, token) => runtime.SubmitPromptAsync(command.Message, command.Images, "rpc", token), live.LifetimeToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Prompt failed for {ServerSessionId}", live.Id);
                if (sendAsync is not null)
                {
                    try { await sendAsync(ServerResponse.Fail(command.Id, command.Type, "prompt_failed", ex.Message, new { serverSessionId = live.Id }), CancellationToken.None); }
                    catch (Exception sendEx) { logger.LogDebug(sendEx, "Failed to send prompt failure for {ServerSessionId}", live.Id); }
                }
            }
        }, CancellationToken.None);
        return Task.FromResult(ServerResponse.Ok(command.Id, command.Type, new { accepted = true }));
    }

    private async Task<ServerResponse> TextQueueAsync(string json, ServerCommandEnvelope envelope, string commandType, CancellationToken cancellationToken)
    {
        var command = RequirePayload<TextMessageCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        await live.RunExclusiveAsync(async (runtime, token) =>
        {
            var message = AgentMessages.User(command.Message);
            if (commandType == ServerCommandTypes.Steer) runtime.Harness.Steer(message);
            else if (commandType == ServerCommandTypes.FollowUp) runtime.Harness.FollowUp(message);
            else runtime.Harness.QueueNextTurn(message);
            if (command.TriggerIfIdle && runtime.Harness.Phase == PiSharp.Agent.Harness.AgentHarnessPhase.Idle) await runtime.Harness.PromptAsync(string.Empty, token);
        }, cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type);
    }

    private ServerResponse Abort(ServerCommandEnvelope envelope)
    {
        RequireSession(RequiredSessionId(envelope)).RequestAbort();
        return ServerResponse.Ok(envelope.Id, envelope.Type);
    }

    private async Task<ServerResponse> GetStateAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(RequiredSessionId(envelope), out var live))
            return ServerResponse.Fail(envelope.Id, envelope.Type, "session_disposed", "Session was disposed.");
        return ServerResponse.Ok(envelope.Id, envelope.Type, await live.SnapshotAsync(cancellationToken));
    }

    private async Task<ServerResponse> GetMessagesAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var messages = await live.RunExclusiveAsync(async (runtime, token) => (await runtime.Session.BuildContextAsync(token)).Messages, cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, new ServerMessagesResult(live.Id, messages));
    }

    private async Task<ServerResponse> ListSessionsAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<ListSessionsCommand>(json);
        return ServerResponse.Ok(command.Id ?? envelope.Id, command.Type, await registry.ListSessionsAsync(command, cancellationToken));
    }

    private async Task<ServerResponse> SetModelAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SetModelCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(command.Provider, command.ModelId, live.Runtime.Harness.ThinkingLevel));
        await live.RunExclusiveAsync((runtime, token) => runtime.SetModelAsync(selection, "server", token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, selection.Model);
    }

    private async Task<ServerResponse> SetThinkingLevelAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SetThinkingLevelCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        await live.RunExclusiveAsync((runtime, token) => runtime.SetThinkingLevelAsync(command.Level, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type);
    }

    private Task<ServerResponse> CompactAsync(string json, ServerCommandEnvelope envelope, Func<object, CancellationToken, Task>? sendAsync, Task responseSent, CancellationToken cancellationToken)
    {
        var command = RequirePayload<CompactCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        _ = Task.Run(async () =>
        {
            try
            {
                await responseSent;
                await live.RunExclusiveAsync((runtime, token) => runtime.Harness.CompactAsync(command.CustomInstructions, token), live.LifetimeToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Compaction failed for {ServerSessionId}", live.Id);
                if (sendAsync is not null)
                {
                    try { await sendAsync(ServerResponse.Fail(command.Id, command.Type, "compact_failed", ex.Message, new { serverSessionId = live.Id }), CancellationToken.None); }
                    catch (Exception sendEx) { logger.LogDebug(sendEx, "Failed to send compaction failure for {ServerSessionId}", live.Id); }
                }
            }
        }, CancellationToken.None);
        return Task.FromResult(ServerResponse.Ok(command.Id, command.Type, new { accepted = true }));
    }

    private async Task<ServerResponse> NewSessionAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var result = await live.RunExclusiveAsync((runtime, token) => runtime.NewSessionAsync(token), cancellationToken);
        if (result.Cancelled) return ServerResponse.Ok(envelope.Id, envelope.Type, new { cancelled = true, reason = result.Reason });
        return ServerResponse.Ok(envelope.Id, envelope.Type, await live.SnapshotAsync(cancellationToken));
    }

    private async Task<ServerResponse> SwitchSessionAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SwitchSessionCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        var result = await live.RunExclusiveAsync(async (runtime, token) =>
        {
            var session = (await runtime.ListSessionsAsync(null, token)).FirstOrDefault(s => s.Id == command.SessionIdOrPath || s.Path == command.SessionIdOrPath)
                ?? throw new InvalidOperationException($"Session '{command.SessionIdOrPath}' not found.");
            registry.EnsureRuntimeSessionSwitchAllowed(live, session.Id);
            return await runtime.SwitchSessionAsync(session, token);
        }, cancellationToken);
        if (result.Cancelled) return ServerResponse.Ok(command.Id, command.Type, new { cancelled = true, reason = result.Reason });
        return ServerResponse.Ok(command.Id, command.Type, await live.SnapshotAsync(cancellationToken));
    }

    private async Task<ServerResponse> ForkAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<ForkSessionCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        var result = await registry.RunWithReservedRuntimeSessionIdAsync(live, command.NewSessionId, async () =>
        {
            return await live.RunExclusiveAsync((runtime, token) => runtime.ForkAsync(runtime.Session.Metadata, new SessionForkOptions(command.EntryId, Id: command.NewSessionId), token), cancellationToken);
        }, cancellationToken);
        if (result.Cancelled) return ServerResponse.Ok(command.Id, command.Type, new { cancelled = true, reason = result.Reason });
        return ServerResponse.Ok(command.Id, command.Type, await live.SnapshotAsync(cancellationToken));
    }

    private async Task<ServerResponse> SetSessionNameAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SetSessionNameCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        await live.RunExclusiveAsync((runtime, token) => runtime.Harness.SetSessionNameAsync(command.Name, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, await live.SnapshotAsync(cancellationToken));
    }

    private async Task<ServerResponse> RunCommandAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<RunCommandRequest>(json);
        var live = RequireSession(command.ServerSessionId);
        if (delegates?.RunCommandAsync is null) return ServerResponse.Fail(command.Id, command.Type, "not_available", $"Command '{command.Type}' is not available on this daemon.");
        var result = await live.RunExclusiveAsync((runtime, token) => delegates.RunCommandAsync(new PiServerHostContext(live, Bridge), command.Text, command.Options, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> CompleteCommandAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<CompleteCommandRequest>(json);
        RequireSession(command.ServerSessionId);
        if (delegates?.CompleteCommandAsync is null) return ServerResponse.Fail(command.Id, command.Type, "not_available", $"Command '{command.Type}' is not available on this daemon.");
        var completions = await delegates.CompleteCommandAsync(command.Text, cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, completions);
    }

    private async Task<ServerResponse> ProcessInputAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<ProcessInputRequest>(json);
        var live = RequireSession(RequiredSessionId(envelope));
        if (delegates?.ProcessInputAsync is null) return ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", $"Command '{envelope.Type}' is not available on this daemon.");
        var result = await live.RunExclusiveAsync((runtime, token) => delegates.ProcessInputAsync(command, token), cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, result);
    }

    private Task<ServerResponse> UiResponseAsync(string json, ServerCommandEnvelope envelope)
    {
        var command = RequirePayload<UiResponseCommand>(json);
        RequireSession(command.ServerSessionId);
        Bridge.ResolveUiAsync(command.RequestId, command.Value, command.Cancelled);
        return Task.FromResult(ServerResponse.Ok(command.Id, command.Type));
    }

    private Task<ServerResponse> GetThemeAsync(ServerCommandEnvelope envelope)
    {
        RequireSession(RequiredSessionId(envelope));
        var document = Themes.ActiveDocument;
        return Task.FromResult(document is null
            ? ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "No theme is configured on this daemon.")
            : ServerResponse.Ok(envelope.Id, envelope.Type, document));
    }

    private Task<ServerResponse> ListThemesAsync(ServerCommandEnvelope envelope)
    {
        if (!string.IsNullOrWhiteSpace(envelope.ServerSessionId)) RequireSession(envelope.ServerSessionId);
        var names = Themes.Documents.Select(document => new ServerThemeSummary(document.Name)).ToArray();
        return Task.FromResult(ServerResponse.Ok(envelope.Id, envelope.Type, names));
    }

    private async Task<ServerResponse> SetThemeAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SetThemeCommand>(json);
        if (!string.IsNullOrWhiteSpace(command.ServerSessionId)) RequireSession(command.ServerSessionId);
        if (!Themes.TrySetActive(command.Name))
            return ServerResponse.Fail(command.Id ?? envelope.Id, command.Type, "theme_not_found", $"Unknown theme '{command.Name}'.");

        await ThemeRegistry.ApplyToSessionsAsync(Themes, registry.Sessions, command.Name!, cancellationToken);
        return ServerResponse.Ok(command.Id ?? envelope.Id, command.Type);

    }

    private async Task<ServerResponse> SetPlanModeAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SetPlanModeCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetPlanModeControl(live) is not { } control)
            return ServerResponse.Fail(command.Id, command.Type, "plan_mode_unavailable", "The plan-mode extension is not loaded in this session.");
        var state = await live.RunExclusiveAsync((runtime, token) => control.ApplyPhaseAsync(command.Phase, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, ToPlanModeState(state));
    }

    private Task<ServerResponse> GetPlanModeAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        if (TryGetPlanModeControl(live) is not { } control)
            return Task.FromResult(ServerResponse.Fail(envelope.Id, envelope.Type, "plan_mode_unavailable", "The plan-mode extension is not loaded in this session."));
        return Task.FromResult(ServerResponse.Ok(envelope.Id, envelope.Type, ToPlanModeState(control.Current)));
    }

    /// <summary>Resolves the plan-mode extension's daemon bridge from the session runtime's loaded extensions.</summary>
    private static IPlanModeDaemonSurface? TryGetPlanModeControl(LiveServerSession live)
        => live.Runtime.ExtensionManager?.Loaded.FirstOrDefault(extension => extension.Descriptor.Id == "plan-mode")?.Instance as IPlanModeDaemonSurface;

    private static ServerPlanModeState ToPlanModeState(ExtensionPlanModeState state)
        => new(state.Phase, state.RestrictedToolNames, state.PlanningModel, state.PlanFile);

    // --- P23: continuity daemon wire handlers (plan C5 §4.9) ---
    // Each handler resolves the session's IContinuitySessionService (the extension implements the
    // interface, mirroring the IPlanModeDaemonSurface pattern) and delegates inside RunExclusiveAsync.
    // When the continuity extension is not loaded, the response code is "continuity_unavailable".

    private async Task<ServerResponse> SetGoalAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<SetGoalCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var result = await live.RunExclusiveAsync((runtime, token) => service.SetGoalAsync(command.Objective, command.MaxTokens, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> GetGoalAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<GetGoalCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var result = await live.RunExclusiveAsync((runtime, token) => service.GetGoalAsync(token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> ScheduleJobAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<ScheduleJobCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var result = await live.RunExclusiveAsync((runtime, token) => service.ScheduleJobAsync(command.Name, command.Cron, command.Prompt, command.Enabled, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> ListJobsAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<ListJobsCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var result = await live.RunExclusiveAsync((runtime, token) => service.ListJobsAsync(token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> CancelJobAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<CancelJobCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var result = await live.RunExclusiveAsync((runtime, token) => service.CancelJobAsync(command.JobId, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> AutonomousAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<AutonomousCommandRequest>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var autonomousCmd = new AutonomousCommand(command.Message, command.MaxTurns, command.MaxTokens, command.TimeoutMinutes, command.Gates);
        var result = await live.RunExclusiveAsync((runtime, token) => service.StartAutonomousAsync(autonomousCmd, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> GetContinuityStateAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<GetContinuityStateCommand>(json);
        var live = RequireSession(command.ServerSessionId);
        if (TryGetContinuityService(live) is not { } service)
            return ServerResponse.Fail(command.Id, command.Type, "continuity_unavailable", "The continuity extension is not loaded in this session.");
        var result = await live.RunExclusiveAsync((runtime, token) => service.GetStateAsync(token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    /// <summary>Resolves the continuity extension's service facade from the session runtime's loaded extensions.</summary>
    private static IContinuitySessionService? TryGetContinuityService(LiveServerSession live)
        => live.Runtime.ExtensionManager?.Loaded.FirstOrDefault(extension => extension.Descriptor.Id == "pisharp-continuity")?.Instance as IContinuitySessionService;

    private async Task<ServerResponse> GetSessionSnapshotAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var snapshot = await live.RunExclusiveAsync(async (runtime, token) => new ServerSessionSnapshot(
            runtime.Session.Metadata.Id,
            runtime.Session.Metadata.Path,
            await runtime.Session.GetSessionNameAsync(token),
            await runtime.GetForkableEntriesAsync(token)), cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, snapshot);
    }

    private async Task<ServerResponse> GetForkMessagesAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var entries = await live.RunExclusiveAsync((runtime, token) => runtime.GetForkableEntriesAsync(token), cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, new { entries });
    }

    private Task<ServerResponse> GetExtensionLoadStatusAsync(ServerCommandEnvelope envelope)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        return Task.FromResult(ServerResponse.Ok(envelope.Id, envelope.Type, live.Runtime.GetExtensionLoadSummary()));
    }

    private Task<ServerResponse> GetExtensionShortcutsAsync(ServerCommandEnvelope envelope)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var manager = live.Runtime.ExtensionManager;
        return Task.FromResult(manager is null
            ? ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "No extension manager is configured for this session.")
            : ServerResponse.Ok(envelope.Id, envelope.Type, manager.Registry.Shortcuts));
    }

    private Task<ServerResponse> GetExtensionRegistryAsync(ServerCommandEnvelope envelope)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var manager = live.Runtime.ExtensionManager;
        return Task.FromResult(manager is null
            ? ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "No extension manager is configured for this session.")
            : ServerResponse.Ok(envelope.Id, envelope.Type, manager.Registry));
    }

    private Task<ServerResponse> ResolveToolAsync(string json, ServerCommandEnvelope envelope)
    {
        var command = RequirePayload<ResolveToolRequest>(json);
        var live = RequireSession(command.ServerSessionId);
        var manager = live.Runtime.ExtensionManager;
        if (manager is null) return Task.FromResult(ServerResponse.Fail(command.Id, command.Type, "not_available", "No extension manager is configured for this session."));
        var tool = manager.Registry.Tools.FirstOrDefault(t => string.Equals(t.Value.Name, command.Name, StringComparison.Ordinal))?.Value;
        return Task.FromResult(tool is null
            ? ServerResponse.Fail(command.Id, command.Type, "not_available", $"Tool '{command.Name}' is not registered.")
            : ServerResponse.Ok(command.Id, command.Type, tool));
    }

    private async Task<ServerResponse> CycleThinkingLevelAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var next = await live.RunExclusiveAsync(async (runtime, token) =>
        {
            var level = RuntimeModelSelector.CycleThinking(runtime.Harness.Model, runtime.Harness.ThinkingLevel, +1);
            await runtime.SetThinkingLevelAsync(level, token);
            return level;
        }, cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, new { level = next.ToString().ToLowerInvariant() });
    }

    private static ServerResponse GetAvailableModels(ServerCommandEnvelope envelope)
        => ServerResponse.Ok(envelope.Id, envelope.Type, PublicApi.Models.Select(m => m.Descriptor).ToArray());

    private static ServerResponse GetCommandsNotAvailable(ServerCommandEnvelope envelope)
        => ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "Command discovery is not available on this daemon.");

    private async Task<ServerResponse> GetLastAssistantTextAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var messages = await live.RunExclusiveAsync(async (runtime, token) => (await runtime.Session.BuildContextAsync(token)).Messages, cancellationToken);
        var text = string.Concat(messages.OfType<AssistantMessage>().LastOrDefault()?.Content.OfType<TextContent>().Select(c => c.Text) ?? []);
        return ServerResponse.Ok(envelope.Id, envelope.Type, new { text });
    }

    private async Task<ServerResponse> GetStartupMessagesAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        RequireSession(RequiredSessionId(envelope));
        if (delegates?.GetStartupMessagesAsync is null) return ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", $"Command '{envelope.Type}' is not available on this daemon.");
        var messages = await delegates.GetStartupMessagesAsync(cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, messages);
    }

    private async Task<ServerResponse> PostStartupChecksAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        if (delegates?.PostStartupChecksAsync is null) return ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", $"Command '{envelope.Type}' is not available on this daemon.");
        await delegates.PostStartupChecksAsync(message => live.EmitEvent(AgentSessionEvent.FromServer("system_message", new { text = message })), cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type);
    }

    private async Task<ServerResponse> GetMcpStatusAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        if (delegates?.GetMcpStatusAsync is null) return ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", $"Command '{envelope.Type}' is not available on this daemon.");
        var status = await delegates.GetMcpStatusAsync(cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, status);
    }

    // --- P04: extension package + managed-skill daemon commands (GAP-55/GAP-56) ---

    private async Task<ServerResponse> InstallExtensionAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<InstallExtensionCommand>(json);
        var live = RequireSession(command.ServerSessionId ?? RequiredSessionId(envelope));
        var binding = live.Runtime.ExtensionBinding;
        var result = await live.RunExclusiveAsync((runtime, token) => binding.InstallExtensionAsync(command.Reference ?? string.Empty, command.Local, command.Force, command.Offline, token), cancellationToken);
        if (result.Success) EmitPackagesChanged(live, added: [command.Reference ?? string.Empty]);
        return ServerResponse.Ok(command.Id, envelope.Type, result);
    }

    private async Task<ServerResponse> UpdateExtensionAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<UpdateExtensionCommand>(json);
        var live = RequireSession(command.ServerSessionId ?? RequiredSessionId(envelope));
        var binding = live.Runtime.ExtensionBinding;
        var request = new ExtensionPackageUpdateRequest(command.Source, command.Extensions, command.ExtensionSource, command.Force, command.Offline);
        var result = await live.RunExclusiveAsync((runtime, token) => binding.UpdateExtensionAsync(request, token), cancellationToken);
        if (result.Success) EmitPackagesChanged(live, updated: [request.Source ?? request.ExtensionSource ?? "*"]);
        return ServerResponse.Ok(command.Id, envelope.Type, result);
    }

    private async Task<ServerResponse> RemoveExtensionAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<RemoveExtensionCommand>(json);
        var live = RequireSession(command.ServerSessionId ?? RequiredSessionId(envelope));
        var binding = live.Runtime.ExtensionBinding;
        var removed = await live.RunExclusiveAsync((runtime, token) => binding.RemoveExtensionAsync(command.Reference ?? string.Empty, command.Local, token), cancellationToken);
        if (removed) EmitPackagesChanged(live, removed: [command.Reference ?? string.Empty]);
        return ServerResponse.Ok(command.Id, envelope.Type, removed);
    }

    private async Task<ServerResponse> ListInstalledExtensionsAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var packages = await live.RunExclusiveAsync((runtime, token) => runtime.ExtensionBinding.ListInstalledExtensionsAsync(token), cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, packages);
    }

    private async Task<ServerResponse> ManageSkillAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = RequirePayload<ManageSkillCommand>(json);
        var live = RequireSession(command.ServerSessionId ?? RequiredSessionId(envelope));
        var binding = live.Runtime.ExtensionBinding;
        switch (command.Op)
        {
            case "create":
            {
                var request = new ManagedSkillCreateRequest(command.Name ?? string.Empty, command.Description ?? string.Empty, command.Content ?? string.Empty, command.DisableModelInvocation ?? false);
                var created = await live.RunExclusiveAsync((runtime, token) => binding.ManagedSkillCreateAsync(request, token), cancellationToken);
                EmitSkillsChanged(live, added: [created.Name]);
                return ServerResponse.Ok(command.Id, envelope.Type, created);
            }
            case "update":
            {
                var request = new ManagedSkillUpdateRequest(command.Description, command.Content, command.DisableModelInvocation);
                var updated = await live.RunExclusiveAsync((runtime, token) => binding.ManagedSkillUpdateAsync(command.Name ?? string.Empty, request, token), cancellationToken);
                EmitSkillsChanged(live, updated: [updated.Name]);
                return ServerResponse.Ok(command.Id, envelope.Type, updated);
            }
            case "delete":
            {
                var deleted = await live.RunExclusiveAsync((runtime, token) => binding.ManagedSkillDeleteAsync(command.Name ?? string.Empty, token), cancellationToken);
                if (deleted) EmitSkillsChanged(live, removed: [command.Name ?? string.Empty]);
                return ServerResponse.Ok(command.Id, envelope.Type, deleted);
            }
            case "list":
            {
                var skills = await live.RunExclusiveAsync((runtime, token) => binding.ManagedSkillListAsync(token), cancellationToken);
                return ServerResponse.Ok(command.Id, envelope.Type, skills);
            }
            case "promote":
            {
                var promoted = await live.RunExclusiveAsync((runtime, token) => binding.ManagedSkillPromoteAsync(command.SourceReference ?? string.Empty, token), cancellationToken);
                EmitSkillsChanged(live, added: [promoted.Name]);
                return ServerResponse.Ok(command.Id, envelope.Type, promoted);
            }
            default:
                return ServerResponse.Fail(command.Id, envelope.Type, "invalid_command", $"Unknown manage_skill op '{command.Op}'. Expected create|update|delete|list|promote.");
        }
    }

    private async Task<ServerResponse> GetSkillsAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var definitions = await live.RunExclusiveAsync((runtime, token) => runtime.ExtensionBinding.GetAllSkillsAsync(token), cancellationToken);
        var skills = definitions
            .Select(skill => new ServerSkillInfo(skill.Name, skill.Description, skill.Source, skill.SourcePriority, skill.Hide, skill.AlwaysApply, skill.DisableModelInvocation, skill.Globs))
            .ToArray();
        return ServerResponse.Ok(envelope.Id, envelope.Type, skills);
    }

    private void EmitPackagesChanged(LiveServerSession live, IReadOnlyList<string>? added = null, IReadOnlyList<string>? removed = null, IReadOnlyList<string>? updated = null)
        => live.EmitEvent(AgentSessionEvent.FromServer(ExtensionEventNames.PackagesChanged, new { added = added ?? [], removed = removed ?? [], updated = updated ?? [] }));

    private void EmitSkillsChanged(LiveServerSession live, IReadOnlyList<string>? added = null, IReadOnlyList<string>? removed = null, IReadOnlyList<string>? updated = null)
        => live.EmitEvent(AgentSessionEvent.FromServer(ExtensionEventNames.SkillsChanged, new { added = added ?? [], removed = removed ?? [], updated = updated ?? [] }));

    /// <summary>
    /// P25 C6: pull-based daemon-global telemetry aggregate. With telemetry disabled (no
    /// aggregator wired) the canonical <see cref="MetricsSnapshot.Disabled"/> payload is returned.
    /// </summary>
    private Task<ServerResponse> GetMetricsAsync(ServerCommandEnvelope envelope)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(metrics is null
            ? ServerResponse.Ok(envelope.Id, envelope.Type, MetricsSnapshot.Disabled(now))
            : ServerResponse.Ok(envelope.Id, envelope.Type, metrics.BuildSnapshot(now)));
    }

    /// <summary>
    /// P25 C6: per-session counters computed from the live session's message context and
    /// retained event log (typed, no telemetry dependency). Reads ride the operation gate exactly
    /// like <c>get_messages</c> because building the message context is a session-scoped read.
    /// </summary>
    private async Task<ServerResponse> GetSessionStatsAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var messages = await live.RunExclusiveAsync(async (runtime, token) => (await runtime.Session.BuildContextAsync(token)).Messages, cancellationToken);
        var snapshot = await live.SnapshotAsync(cancellationToken);
        var replay = live.EventLog.ReplayFrom(0);
        var retries = replay.Events.Count(env => string.Equals(env.Event.Type, "auto_retry_start", StringComparison.Ordinal));
        var assistants = messages.OfType<AssistantMessage>().ToArray();
        var toolResults = messages.OfType<ToolResultMessage>().ToArray();

        var stats = new SessionStats(
            live.Id,
            live.RuntimeSessionId,
            assistants.LastOrDefault()?.Model ?? snapshot.Model.Id,
            snapshot.MessageCount,
            assistants.Length,
            assistants.Sum(message => message.Usage?.Input ?? 0),
            assistants.Sum(message => message.Usage?.Output ?? 0),
            toolResults.Length,
            toolResults.Count(result => result.IsError),
            retries,
            messages.FirstOrDefault()?.Timestamp ?? live.Runtime.Session.Metadata.CreatedAt,
            live.LastActivityUtc);
        return ServerResponse.Ok(envelope.Id, envelope.Type, stats);
    }

    private Task<ServerResponse> ShutdownAsync(string json, ServerCommandEnvelope envelope, Task responseSent)
    {
        var request = RequirePayload<ShutdownRequest>(json);
        var onShutdown = delegates?.OnShutdown;
        if (onShutdown is null)
            return Task.FromResult(ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "Shutdown is not available on this server."));
        if (!request.Confirm)
            return Task.FromResult(ServerResponse.Fail(envelope.Id, envelope.Type, "confirmation_required", "Shutdown requires an explicit confirm flag to stop the daemon."));

        // Respond first, then stop the host once the response has been flushed to the client.
        _ = Task.Run(async () =>
        {
            try
            {
                await responseSent;
                await onShutdown(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Graceful shutdown failed");
            }
        }, CancellationToken.None);

        return Task.FromResult(ServerResponse.Ok(envelope.Id, envelope.Type, new ShutdownResult(true)));
    }

    private LiveServerSession RequireSession(string serverSessionId)
        => registry.TryGet(serverSessionId, out var live) ? live : throw new InvalidOperationException($"Server session '{serverSessionId}' not found.");

    private static string RequiredSessionId(ServerCommandEnvelope envelope)
        => string.IsNullOrWhiteSpace(envelope.ServerSessionId) ? throw new InvalidOperationException("serverSessionId is required.") : envelope.ServerSessionId;

    /// <summary>
    /// Deserializes a command payload, converting a null result (missing/invalid JSON body)
    /// into a <see cref="JsonException"/> that surfaces as an <c>invalid_json</c> failure.
    /// </summary>
    private static T RequirePayload<T>(string json) where T : class
        => JsonSerializer.Deserialize<T>(json, ServerJsonSerializer.Options)
           ?? throw new JsonException($"Command payload for '{typeof(T).Name}' must not be null.");

    private async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > _maxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, $"Message exceeds the {_maxMessageBytes} byte limit.", CancellationToken.None);
                return null;
            }
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
