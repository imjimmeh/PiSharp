using PiSharp.Agent.Core.Events;
using PiSharp.Client;
namespace PiSharp.Sdk;

/// <summary>Kind of a single <see cref="ClientMessageRow"/> in the SDK transcript view.</summary>
public enum ClientMessageRowKind
{
    User,
    Assistant,
    Thinking,
    ToolCall,
    ToolResult,
    System,
    Error,
    Custom,
    Unknown,
}

/// <summary>
/// A single row in the SDK's read-only transcript view. Message rows are keyed by
/// <see cref="EntryId"/>; tool rows additionally carry <see cref="ToolCallId"/>.
/// </summary>
public sealed record ClientMessageRow(
    ClientMessageRowKind Kind,
    string? EntryId,
    string? ToolCallId,
    object? Payload);

/// <summary>
/// Read-only public view of the daemon session state, built on demand from the P01
/// <see cref="PiSharp.Client.ClientSessionState"/> reducer snapshot. The underlying reducer stays
/// internal to <see cref="PiSharp.Client"/>; this view never mutates it.
/// </summary>
public sealed class ClientSessionStateView
{
    private readonly PiSharp.Client.ClientSessionState _inner;
    private readonly string _serverSessionId;
    private readonly long _headSequence;
    private readonly bool _attached;
    private readonly long _lastAppliedSequence;
    private readonly IReadOnlyList<ClientMessageRow> _transcript;
    private readonly IReadOnlyList<AgentSessionEvent> _pendingToolCalls;

    internal ClientSessionStateView(
        PiSharp.Client.ClientSessionState inner,
        string serverSessionId,
        long headSequence,
        bool attached,
        IReadOnlyList<AgentSessionEvent> pendingToolCalls)
    {
        _inner = inner;
        _serverSessionId = serverSessionId;
        _headSequence = headSequence;
        _attached = attached;
        _lastAppliedSequence = inner.LastAppliedSequence;
        _transcript = MapTranscript(inner.Transcript);
        _pendingToolCalls = pendingToolCalls;
    }

    public string ServerSessionId => _serverSessionId;
    public long HeadSequence => _headSequence;
    public long LastAppliedSequence => _lastAppliedSequence;
    public bool Attached => _attached;
    public bool IsBusy => _inner.IsBusy;
    public bool IsCompacting => _inner.IsCompacting;
    public string Status => _inner.Status;

    /// <summary>Display name of the currently selected model (falls back to the model id).</summary>
    public string? ModelId => string.IsNullOrWhiteSpace(_inner.ModelDisplay) ? null : _inner.ModelDisplay;

    /// <summary>Current thinking level as a lowercase string (e.g. <c>"off"</c>, <c>"minimal"</c>).</summary>
    public string ThinkingLevel => _inner.ThinkingLevel;

    public string? SessionName => _inner.SessionName;

    /// <summary>Transcript rows in apply order (message rows by entry id, tool rows by tool-call id).</summary>
    public IReadOnlyList<ClientMessageRow> TranscriptItems => _transcript;

    /// <summary>Tool executions currently in flight (tool_execution_start without a matching end).</summary>
    public IReadOnlyList<AgentSessionEvent> PendingToolCalls => _pendingToolCalls;

    internal PiSharp.Client.ClientSessionState Inner => _inner;

    private static IReadOnlyList<ClientMessageRow> MapTranscript(IReadOnlyList<ClientTranscriptItem> transcript)
    {
        var rows = new List<ClientMessageRow>(transcript.Count);
        foreach (var item in transcript)
        {
            rows.Add(new ClientMessageRow(
                Classify(item),
                item.EntryId,
                item.ToolCallId,
                new TranscriptRowPayload(item)));
        }

        return rows;
    }

    private static ClientMessageRowKind Classify(ClientTranscriptItem item)
    {
        if (item.IsTool)
        {
            if (item.ToolIsError) return ClientMessageRowKind.Error;
            return item.ToolResult is not null ? ClientMessageRowKind.ToolResult : ClientMessageRowKind.ToolCall;
        }

        return item.Role switch
        {
            "user" => ClientMessageRowKind.User,
            "assistant" => item.IsPending ? ClientMessageRowKind.Thinking : ClientMessageRowKind.Assistant,
            "system" => ClientMessageRowKind.System,
            "error" => ClientMessageRowKind.Error,
            _ => ClientMessageRowKind.Unknown,
        };
    }

    /// <summary>
    /// Raw payload of a transcript row, exposing the full P01 <see cref="ClientTranscriptItem"/>
    /// (role, text, pending flag, tool name/result, arguments) without coupling the SDK surface to
    /// the client assembly's record.
    /// </summary>
    public sealed record TranscriptRowPayload(ClientTranscriptItem Item);
}
