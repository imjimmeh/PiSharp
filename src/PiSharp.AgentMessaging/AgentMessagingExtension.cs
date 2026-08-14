using System.Text.Json;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "agent-messaging",
    Name = "PiSharp Agent Messaging",
    Version = "0.1.0",
    Description = "Agent-to-agent messaging and coordination: family roster, the hub tool (list/send/watch/steer), the agent_message child-session surface, and a prompt brief over the daemon event lane.",
    SourceId = "pi:extension:agent-messaging")]

namespace PiSharp.AgentMessaging;

/// <summary>
/// <c>agent-messaging</c> extension entry. Reads the
/// <c>extensions.agent-messaging.*</c> settings, builds the in-process roster +
/// router + persisted outbox, registers the <c>hub</c> and
/// <c>agent_message</c> tools plus the messaging brief, and bridges message
/// and roster events onto the daemon wire (C3 custom-event lane).
/// </summary>
public sealed class AgentMessagingExtension : IExtension, IAsyncDisposable
{
    internal const string SkillName = "agent_message";
    internal const string BriefSectionId = AgentMessagingBriefFormatter.BriefSectionId;
    private const int BriefInboxLimit = 5;

    private static readonly string[] SubagentCreatedEvents = ["subagents:created"];
    private static readonly string[] SubagentEndEvents = ["subagents:completed", "subagents:failed", "subagents:cancelled"];

    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private CancellationTokenSource _lifetimeCts = new();

    private IExtensionApi? _api;
    private AgentMessagingOptions _options = new();
    private AgentRosterService? _roster;
    private AgentMessageStore? _store;
    private AgentMessageRouter? _router;
    private AgentMessageEventAdapter? _adapter;
    private string? _agentId;
    private bool _disposed;

    /// <summary>In-process roster (family registry) — tests and hosts reach through this.</summary>
    internal AgentRosterService? Roster { get { lock (_gate) return _roster; } }

    /// <summary>In-process router — tests reach through this.</summary>
    internal AgentMessageRouter? Router { get { lock (_gate) return _router; } }

    /// <summary>This session's agent id (roster key).</summary>
    internal string? AgentId { get { lock (_gate) return _agentId; } }

    /// <summary>Effective settings after initialization.</summary>
    internal AgentMessagingOptions Options { get { lock (_gate) return _options; } }

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _options = AgentMessagingOptions.Read(api, DefaultStoreDirectory());
        if (!_options.Enabled)
            return; // master switch off — register nothing

        var agentId = await ResolveAgentIdAsync(api, cancellationToken).ConfigureAwait(false);
        var roster = new AgentRosterService();
        var store = new AgentMessageStore(_options.StoreDirectory ?? DefaultStoreDirectory());
        var adapter = new AgentMessageEventAdapter(api, agentId);
        var router = new AgentMessageRouter(roster, store, _options, adapter.DeliverAsync);

        lock (_gate)
        {
            _agentId = agentId;
            _roster = roster;
            _store = store;
            _adapter = adapter;
            _router = router;
        }

        roster.Register(new AgentInfo(
            AgentId: agentId,
            Name: await SafeGetSessionNameAsync(api, cancellationToken).ConfigureAwait(false),
            Role: "main",
            ParentAgentId: null,
            Status: AgentStatus.Running,
            Cwd: api.Cwd,
            Model: null,
            ThinkingLevel: null,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow));

        var lifetimeToken = _lifetimeCts.Token;
        roster.Changed += snapshot =>
        {
            _ = PublishRosterAsync(adapter, snapshot, lifetimeToken);
        };

        Subscribe(api, ExtensionEventNames.AgentStart, (_, ct) => OnAgentStartAsync(agentId, roster, ct));
        Subscribe(api, ExtensionEventNames.AgentEnd, (_, _) => OnAgentEndAsync(agentId, roster));
        Subscribe(api, ExtensionEventNames.SessionShutdown, (_, _) => OnSessionShutdownAsync());
        foreach (var eventName in SubagentCreatedEvents)
            Subscribe(api, eventName, (evt, ct) => OnSubagentEventAsync(evt, agentId, roster, create: true, ct));
        foreach (var eventName in SubagentEndEvents)
            Subscribe(api, eventName, (evt, ct) => OnSubagentEventAsync(evt, agentId, roster, create: false, ct));

        api.RegisterTool(new ExtensionToolRegistration(
            HubTool.ToolName,
            HubTool.ToolName,
            "Agent roster and messaging hub: list family agents, send/steer messages, watch agent status.",
            HubTool.BuildSchema(),
            (toolCallId, parameters, ct, onUpdate) => new HubTool(agentId, roster, router).ExecuteAsync(toolCallId, parameters, ct, onUpdate)));

        api.RegisterTool(new ExtensionToolRegistration(
            AgentMessageTool.ToolName,
            AgentMessageTool.ToolName,
            "Send agent-to-agent messages (receiver: 'parent', an agent id, or 'all') and read this agent's inbox.",
            AgentMessageTool.BuildSchema(),
            (toolCallId, parameters, ct, onUpdate) => new AgentMessageTool(agentId, roster, router).ExecuteAsync(toolCallId, parameters, ct, onUpdate)));

        api.RegisterSkill(new ExtensionSkillDefinition(
            SkillName,
            "Send and read agent-to-agent messages with the hub and agent_message surfaces.",
            SkillContent,
            FilePath: $"extension://{api.Descriptor.Id}/skills/agent_message.md"));

        if (_options.BriefInPrompt)
        {
            Subscribe(api, ExtensionEventNames.BeforePromptRender, (evt, ct) => OnBeforePromptRenderAsync(evt, agentId, roster, router, ct));
        }

        // Replay persisted queued messages (outbox survives daemon restarts).
        await router.ReplayAsync(cancellationToken).ConfigureAwait(false);
        await router.CleanupExpiredAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        IDisposable[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
            subscription.Dispose();

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();

        if (_roster is not null && _agentId is not null)
            _roster.Remove(_agentId);

        if (_router is not null)
            await _router.DisposeAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _router = null;
            _store = null;
            _adapter = null;
            _roster = null;
        }
    }

    // --- Event handlers ---

    private static Task OnAgentStartAsync(string agentId, AgentRosterService roster, CancellationToken ct)
    {
        roster.UpdateStatus(agentId, AgentStatus.Running, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    private static Task OnAgentEndAsync(string agentId, AgentRosterService roster)
    {
        roster.UpdateStatus(agentId, AgentStatus.Gone, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    private Task OnSessionShutdownAsync()
        => DisposeAsync().AsTask();

    private static Task OnSubagentEventAsync(ExtensionEvent evt, string parentAgentId, AgentRosterService roster, bool create, CancellationToken ct)
    {
        var subagentId = TryReadSubagentId(evt.Payload);
        if (subagentId is null)
            return Task.CompletedTask;

        if (create)
        {
            roster.Register(new AgentInfo(
                AgentId: subagentId,
                Name: null,
                Role: "subagent",
                ParentAgentId: parentAgentId,
                Status: AgentStatus.Running,
                Cwd: string.Empty,
                Model: null,
                ThinkingLevel: null,
                CreatedAt: DateTimeOffset.UtcNow,
                LastActiveAt: DateTimeOffset.UtcNow));
        }
        else
        {
            roster.UpdateStatus(subagentId, AgentStatus.Gone, DateTimeOffset.UtcNow);
        }

        return Task.CompletedTask;
    }

    private async Task OnBeforePromptRenderAsync(ExtensionEvent evt, string agentId, AgentRosterService roster, AgentMessageRouter router, CancellationToken ct)
    {
        try
        {
            var family = roster.GetFamily(agentId);
            var unread = router.GetInbox(agentId, limit: BriefInboxLimit);
            var content = AgentMessagingBriefFormatter.FormatBrief(family, unread.Count > 0 ? unread : null);
            if (content is null)
                return;

            evt.ModifyPromptDocument(new PromptDocumentPatch(
                AppendSections:
                [
                    new PromptDocumentSectionPatch(
                        Id: BriefSectionId,
                        Content: content,
                        Slot: "instructions",
                        Priority: 0,
                        Kind: "extension",
                        ContentType: PromptDocumentContentTypes.Markdown),
                ]));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failed brief must never break prompt rendering.
        }
    }

    private static async Task PublishRosterAsync(AgentMessageEventAdapter adapter, AgentRoster roster, CancellationToken ct)
    {
        try
        {
            await adapter.EmitRosterAsync(roster, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch
        {
            // wire unavailable — roster stays in-process
        }
    }

    // --- Wiring helpers ---

    private void Subscribe(IExtensionApi api, string eventName, ExtensionEventHandler handler)
    {
        var subscription = api.On(eventName, handler);
        lock (_gate)
        {
            if (_disposed)
            {
                subscription.Dispose();
                return;
            }
            _subscriptions.Add(subscription);
        }
    }

    private static async Task<string> ResolveAgentIdAsync(IExtensionApi api, CancellationToken cancellationToken)
    {
        var name = await SafeGetSessionNameAsync(api, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(name)
            ? $"agent-{Environment.ProcessId}-{Guid.NewGuid():N}"
            : name;
    }

    private static async Task<string?> SafeGetSessionNameAsync(IExtensionApi api, CancellationToken cancellationToken)
    {
        try
        {
            return await api.Session.GetNameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string DefaultStoreDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pi", "PiSharp", "agent-messaging");
    }

    private static string? TryReadSubagentId(object? payload)
    {
        if (payload is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
            return null;
        }

        if (payload is JsonDocument document)
        {
            try
            {
                return TryReadSubagentId(document.RootElement);
            }
            finally
            {
                document.Dispose();
            }
        }

        return null;
    }

    private const string SkillContent = """
        # agent_message

        Send and read agent-to-agent messages within your coordination family.

        ## agent_message.send
        - `receiver`: `"parent"`, a sibling agent id from `hub list`, or `"all"`.
        - `body`: the message text (max 8192 chars).
        Replies to the parent by default when `receiver` is `"parent"`.

        ## agent_message.read
        - `since?`: message id to read strictly-newer messages from.
        - `limit?`: maximum number of messages to return.

        Use `hub` (list/send/watch/steer) for the full roster surface.
        """;
}
