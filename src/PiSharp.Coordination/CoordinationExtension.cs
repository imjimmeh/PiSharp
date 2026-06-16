using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp.coordination", Name = "PiSharp Coordination", Version = "0.1.0")]

namespace PiSharp.Coordination;

public sealed class CoordinationExtension : IExtension, IAsyncDisposable
{
    private static readonly JsonElement EmptyObjectSchema = JsonSerializer.Deserialize<JsonElement>("""
    { "type": "object", "properties": {}, "additionalProperties": false }
    """);

    private static readonly JsonElement SendParametersSchema = JsonSerializer.Deserialize<JsonElement>("""
    {
      "type": "object",
      "properties": {
        "to": {
          "type": "string",
          "description": "Target agent id or all. Defaults to all."
        },
        "body": {
          "type": "string",
          "description": "Message body to deliver. Maximum 8192 characters."
        }
      },
      "required": ["body"],
      "additionalProperties": false
    }
    """);

    private static readonly JsonElement InboxParametersSchema = JsonSerializer.Deserialize<JsonElement>("""
    {
      "type": "object",
      "properties": {
        "includeRead": {
          "type": "boolean",
          "description": "When true, include messages already returned by this extension instance. Defaults to false."
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100,
          "description": "Maximum messages to return. Defaults to 20."
        }
      },
      "additionalProperties": false
    }
    """);

    private DaemonConnection? _connection;
    private string? _agentId;
    private DateTimeOffset? _lastInboxReadAt;
    private CancellationTokenSource _lifetimeCts = new();

    internal bool IsLifetimeCancelled => _lifetimeCts.IsCancellationRequested;
    internal bool IsConnectionDisposed => _connection is null;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        DaemonConnection? connection = null;
        try
        {
            connection = await CoordinationDaemonConnector.ConnectOrStartAsync(api.Cwd);
            var repoRoot = connection.RepoRoot;
            _agentId = $"main-{Environment.ProcessId}-{Guid.NewGuid():N}";
            await connection.Client.RegisterAgentAsync(
                new AgentRegistration(_agentId, Environment.ProcessId, null, null, repoRoot),
                cancellationToken);
            _connection = connection;
            connection = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // connection cleanup handled in finally
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
        }

        var daemonConnection = _connection;
        var agentId = _agentId;
        var coordinationRoot = daemonConnection?.RepoRoot ?? CoordinationDaemonConnector.ResolveRepositoryRoot(api.Cwd);
        var lifetimeToken = _lifetimeCts.Token;

        api.RegisterTool(new ExtensionToolRegistration(
            "coordination_roster",
            "coordination_roster",
            "List agents known to the coordination daemon.",
            EmptyObjectSchema,
            (_, _, ct, _) => CoordinationTools.RosterAsync(daemonConnection, ct)));

        api.RegisterTool(new ExtensionToolRegistration(
            "coordination_send",
            "coordination_send",
            "Send a message to a local subagent.",
            SendParametersSchema,
            (_, parameters, ct, _) =>
            {
                var args = CoordinationTools.DeserializeSendArgs(parameters);
                return CoordinationTools.SendAsync(daemonConnection, agentId!, args, ct);
            }));

        api.RegisterTool(new ExtensionToolRegistration(
            "coordination_inbox",
            "coordination_inbox",
            "Check for incoming messages from local subagents.",
            InboxParametersSchema,
            async (_, parameters, ct, _) =>
            {
                var args = CoordinationTools.DeserializeInboxArgs(parameters);
                var result = await CoordinationTools.InboxAsync(daemonConnection, agentId!, args, _lastInboxReadAt, ct);
                _lastInboxReadAt = result.NewCursor ?? _lastInboxReadAt;
                return result.Output;
            }));

        api.On(ExtensionEventNames.BeforePromptRender, async (evt, ct) =>
        {
            if (daemonConnection is null)
                return;

            try
            {
                var roster = await daemonConnection.Client.GetRosterAsync(ct);
                var unread = (await daemonConnection.Client.GetInboxAsync(agentId!, sinceTimestamp: _lastInboxReadAt, ct)).Messages;

                var content = CoordinationBriefFormatter.FormatBrief(
                    agents: roster.Agents,
                    messages: unread.Length > 0 ? unread : null);

                if (content is not null)
                {
                    evt.ModifyPromptDocument(new PromptDocumentPatch(
                        AppendSections: [
                            new PromptDocumentSectionPatch(
                                Id: CoordinationBriefFormatter.BriefSectionId,
                                Content: content,
                                Slot: "instructions",
                                Priority: 0,
                                Kind: "extension",
                                ContentType: PromptDocumentContentTypes.Markdown)
                        ]));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // daemon unavailable or brief empty — do not crash prompt rendering
            }
        });

        api.On(ExtensionEventNames.ToolExecutionEnd, async (evt, ct) =>
        {
            if (daemonConnection is null)
                return;

            try
            {
                var inboxResult = await daemonConnection.Client.GetInboxAsync(agentId!, sinceTimestamp: _lastInboxReadAt, ct);
                var messages = inboxResult.Messages;

                if (messages.Length > 0)
                {
                    foreach (var msg in messages)
                    {
                        var contentText = $"[Coordination Message from {msg.FromAgentId}]: {msg.Body}";
                        await api.SendMessageAsync(
                            AgentMessages.User(contentText),
                            ExtensionMessageDelivery.Steer,
                            triggerTurn: false,
                            ct);
                    }

                    var latestTimestamp = messages.Max(m => m.Timestamp);
                    if (latestTimestamp > (_lastInboxReadAt ?? DateTimeOffset.MinValue))
                    {
                        _lastInboxReadAt = latestTimestamp;
                    }
                }
            }
            catch
            {
                // Fail silently during mid-turn background checks to avoid interrupting the agent loop
            }
        });

        api.On(ExtensionEventNames.SessionShutdown, async (_, _) =>
        {
            await CleanupAsync();
        });

        var subagentEventNames = new[] { "subagents:created", "subagents:started", "subagents:completed", "subagents:failed", "subagents:steered", "subagents:compacted" };
        foreach (var eventName in subagentEventNames)
        {
            api.On(eventName, async (evt, ct) =>
            {
                if (daemonConnection is null)
                    return;

                var record = PiSubagentsEventAdapter.TryMap(eventName, evt.Payload, agentId, coordinationRoot);
                if (record is null)
                    return;

                try
                {
                    await daemonConnection.Client.RecordSubagentEventAsync(record, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                }
            });
        }

        api.Use(async (context, next, token) =>
        {
            if (daemonConnection is null)
            {
                await next(context, token);
                return;
            }

            if (context.BeforeToolCall is not null)
            {
                var activity = FileToolActivityParser.Parse(
                    context.BeforeToolCall.ToolCall.Name,
                    context.BeforeToolCall.Args,
                    coordinationRoot);

                if (activity is null)
                {
                    await next(context, token);
                    return;
                }

                if (activity.Kind == FileActivityKind.Read)
                {
                    await RecordFileActivityAsync(daemonConnection, agentId!, activity.FilePath, FileActivityKind.Read, lifetimeToken, token);
                    await next(context, token);
                    return;
                }

                PreflightResponse? preflight = null;
                try
                {
                    preflight = await daemonConnection.Client.PreflightToolAsync(
                        agentId!,
                        context.BeforeToolCall.ToolCall.Name,
                        context.BeforeToolCall.Args,
                        activity.FilePath,
                        token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                }

                if (preflight?.ShouldWarn == true)
                {
                    context.Blocked = true;
                    context.BlockReason = preflight.Message;
                    return;
                }

                await next(context, token);
                return;
            }

            if (context.AfterToolCall is not null)
            {
                var activity = FileToolActivityParser.Parse(
                    context.AfterToolCall.ToolCall.Name,
                    context.AfterToolCall.Args,
                    coordinationRoot);

                if (activity?.Kind == FileActivityKind.Write && context.AfterToolCall.IsError == false)
                {
                    await RecordFileActivityAsync(daemonConnection, agentId!, activity.FilePath, FileActivityKind.Write, lifetimeToken, token);
                }

                await next(context, token);
                return;
            }

            await next(context, token);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
    }

    private void Cleanup()
    {
        if (_lifetimeCts.IsCancellationRequested)
            return;

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private async Task CleanupAsync()
    {
        Cleanup();
        if (_connection is not null)
        {
            var connection = _connection;
            _connection = null;
            if (_agentId is not null)
            {
                try
                {
                    await connection.Client.UnregisterAgentAsync(_agentId);
                }
                catch
                {
                    // best-effort; daemon may already be unreachable
                }
            }
            await connection.DisposeAsync();
        }
    }

    private static async Task RecordFileActivityAsync(
        DaemonConnection connection,
        string agentId,
        string filePath,
        FileActivityKind kind,
        CancellationToken lifetimeToken,
        CancellationToken pipelineToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, pipelineToken);
            if (kind == FileActivityKind.Read)
                await connection.Client.RecordFileReadAsync(agentId, filePath, linkedCts.Token);
            else
                await connection.Client.RecordFileWriteAsync(agentId, filePath, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
