using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Ai;
using PiSharp.Agent.Core.Events;
using PiSharp.Runtime;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.UiBridge;

namespace PiSharp.Server.WebSockets;

public sealed class PiServerWebSocketHandler(
    ServerSessionRegistry registry,
    ApiKeyValidator apiKeys,
    ILogger<PiServerWebSocketHandler> logger,
    IServerUiBridge? uiBridge = null,
    PiServerCommandDelegates? delegates = null)
{
    private IServerUiBridge Bridge => uiBridge ?? NoOpServerUiBridge.Instance;

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
        var eventPumps = new Dictionary<string, Task>(StringComparer.Ordinal);

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
            eventPumps[live.Id] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in live.ReadEventsAsync(sinceSequence, linked.Token)) await SendAsync(evt, linked.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { logger.LogWarning(ex, "Event pump failed for {ServerSessionId}", live.Id); }
            }, CancellationToken.None);
            return Task.CompletedTask;
        }

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, cancellationToken);
                if (message is null) break;
                var responseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var response = await DispatchTextCommandAsync(message, EnsureEventPumpAsync, SendAsync, responseSent.Task, cancellationToken);
                try
                {
                    await SendAsync(response, cancellationToken);
                }
                finally
                {
                    responseSent.TrySetResult();
                }
            }
        }
        finally
        {
            linked.Cancel();
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
        var request = JsonSerializer.Deserialize<CreateServerSessionRequest>(json, ServerJsonSerializer.Options)!;
        var created = await registry.CreateAsync(request, cancellationToken);
        if (ensureEventPump is not null && registry.TryGet(created.ServerSessionId, out var live)) await ensureEventPump(live, 0);
        return ServerResponse.Ok(envelope.Id, envelope.Type, created);
    }

    private async Task<ServerResponse> AttachAsync(string json, ServerCommandEnvelope envelope, Func<LiveServerSession, long, Task>? ensureEventPump, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<AttachCommand>(json, ServerJsonSerializer.Options)!;
        var live = RequireSession(command.ServerSessionId);
        var replay = live.EventLog.ReplayFrom(command.SinceSequence);
        if (ensureEventPump is not null) await ensureEventPump(live, command.SinceSequence);
        return ServerResponse.Ok(envelope.Id, envelope.Type, new AttachResult(live.Id, replay.FromSequence, replay.HeadSequence, replay.Gap, replay.Events.Count));
    }

    private async Task<ServerResponse> DisposeSessionAsync(ServerCommandEnvelope envelope, CancellationToken cancellationToken)
        => ServerResponse.Ok(envelope.Id, envelope.Type, new { disposed = await registry.DisposeAsync(RequiredSessionId(envelope)) });

    private Task<ServerResponse> PromptAsync(string json, ServerCommandEnvelope envelope, Func<object, CancellationToken, Task>? sendAsync, Task responseSent, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<PromptCommand>(json, ServerJsonSerializer.Options)!;
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
        var command = JsonSerializer.Deserialize<TextMessageCommand>(json, ServerJsonSerializer.Options)!;
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
        var command = JsonSerializer.Deserialize<ListSessionsCommand>(json, ServerJsonSerializer.Options)!;
        return ServerResponse.Ok(command.Id ?? envelope.Id, command.Type, await registry.ListSessionsAsync(command, cancellationToken));
    }

    private async Task<ServerResponse> SetModelAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<SetModelCommand>(json, ServerJsonSerializer.Options)!;
        var live = RequireSession(command.ServerSessionId);
        var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(command.Provider, command.ModelId, live.Runtime.Harness.ThinkingLevel));
        await live.RunExclusiveAsync((runtime, token) => runtime.SetModelAsync(selection, "server", token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, selection.Model);
    }

    private async Task<ServerResponse> SetThinkingLevelAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<SetThinkingLevelCommand>(json, ServerJsonSerializer.Options)!;
        var live = RequireSession(command.ServerSessionId);
        await live.RunExclusiveAsync((runtime, token) => runtime.SetThinkingLevelAsync(command.Level, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type);
    }

    private Task<ServerResponse> CompactAsync(string json, ServerCommandEnvelope envelope, Func<object, CancellationToken, Task>? sendAsync, Task responseSent, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<CompactCommand>(json, ServerJsonSerializer.Options)!;
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
        var command = JsonSerializer.Deserialize<SwitchSessionCommand>(json, ServerJsonSerializer.Options)!;
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
        var command = JsonSerializer.Deserialize<ForkSessionCommand>(json, ServerJsonSerializer.Options)!;
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
        var command = JsonSerializer.Deserialize<SetSessionNameCommand>(json, ServerJsonSerializer.Options)!;
        var live = RequireSession(command.ServerSessionId);
        await live.RunExclusiveAsync((runtime, token) => runtime.Harness.SetSessionNameAsync(command.Name, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, await live.SnapshotAsync(cancellationToken));
    }

    private async Task<ServerResponse> RunCommandAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<RunCommandRequest>(json, ServerJsonSerializer.Options)!;
        var live = RequireSession(command.ServerSessionId);
        if (delegates?.RunCommandAsync is null) return ServerResponse.Fail(command.Id, command.Type, "not_available", $"Command '{command.Type}' is not available on this daemon.");
        var result = await live.RunExclusiveAsync((runtime, token) => delegates.RunCommandAsync(new PiServerHostContext(live, Bridge), command.Text, command.Options, token), cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, result);
    }

    private async Task<ServerResponse> CompleteCommandAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<CompleteCommandRequest>(json, ServerJsonSerializer.Options)!;
        RequireSession(command.ServerSessionId);
        if (delegates?.CompleteCommandAsync is null) return ServerResponse.Fail(command.Id, command.Type, "not_available", $"Command '{command.Type}' is not available on this daemon.");
        var completions = await delegates.CompleteCommandAsync(command.Text, cancellationToken);
        return ServerResponse.Ok(command.Id, command.Type, completions);
    }

    private async Task<ServerResponse> ProcessInputAsync(string json, ServerCommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = JsonSerializer.Deserialize<ProcessInputRequest>(json, ServerJsonSerializer.Options)!;
        var live = RequireSession(RequiredSessionId(envelope));
        if (delegates?.ProcessInputAsync is null) return ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", $"Command '{envelope.Type}' is not available on this daemon.");
        var result = await live.RunExclusiveAsync((runtime, token) => delegates.ProcessInputAsync(command, token), cancellationToken);
        return ServerResponse.Ok(envelope.Id, envelope.Type, result);
    }

    private Task<ServerResponse> UiResponseAsync(string json, ServerCommandEnvelope envelope)
    {
        var command = JsonSerializer.Deserialize<UiResponseCommand>(json, ServerJsonSerializer.Options)!;
        RequireSession(command.ServerSessionId);
        Bridge.ResolveUiAsync(command.RequestId, command.Value, command.Cancelled);
        return Task.FromResult(ServerResponse.Ok(command.Id, command.Type));
    }

    private Task<ServerResponse> GetThemeAsync(ServerCommandEnvelope envelope)
    {
        var live = RequireSession(RequiredSessionId(envelope));
        var theme = live.Runtime.Theme;
        return Task.FromResult(theme is null
            ? ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "No theme is configured for this session.")
            : ServerResponse.Ok(envelope.Id, envelope.Type, theme));
    }

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
        var command = JsonSerializer.Deserialize<ResolveToolRequest>(json, ServerJsonSerializer.Options)!;
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

    private Task<ServerResponse> ShutdownAsync(string json, ServerCommandEnvelope envelope, Task responseSent)
    {
        _ = JsonSerializer.Deserialize<ShutdownRequest>(json, ServerJsonSerializer.Options); // validate request shape; confirmation token is optional
        var onShutdown = delegates?.OnShutdown;
        if (onShutdown is null)
            return Task.FromResult(ServerResponse.Fail(envelope.Id, envelope.Type, "not_available", "Shutdown is not available on this server."));

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

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
