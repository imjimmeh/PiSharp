using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Serialization;
using PiSharp.Server.Contracts;

namespace PiSharp.Client;

/// <summary>
/// Pure reducer that folds a single <see cref="ServerEventEnvelope"/> (flat <see cref="AgentSessionEvent"/>)
/// into a new <see cref="ClientSessionState"/>. No I/O, no mutation of the input state.
/// </summary>
public static class ClientEventReducer
{
    public static ClientSessionState Apply(ClientSessionState state, ServerEventEnvelope envelope)
    {
        var evt = envelope.Event;
        var seq = envelope.Sequence;
        return evt.Type switch
        {
            "message_start" => ApplyMessage(state, Payload<MessagePayload>(evt.Data)?.Message, isPending: true, seq),
            "message_update" => ApplyMessage(state, Payload<MessagePayload>(evt.Data)?.Message, isPending: true, seq),
            "message_end" => ApplyMessage(state, Payload<MessagePayload>(evt.Data)?.Message, isPending: false, seq),
            "tool_execution_start" => ApplyToolStart(state, Payload<ToolExecutionStartPayload>(evt.Data), seq),
            "tool_execution_update" => ApplyToolUpdate(state, Payload<ToolExecutionUpdatePayload>(evt.Data), seq),
            "tool_execution_end" => ApplyToolEnd(state, Payload<ToolExecutionEndPayload>(evt.Data), seq),
            "agent_end" => state with { IsBusy = false, Status = "Idle", LastAppliedSequence = seq },
            "model_select" => ApplyModelSelect(state, Payload<ModelSelectPayload>(evt.Data), seq),
            "compaction_start" => state with { IsCompacting = true, IsBusy = true, Status = "Compacting", LastAppliedSequence = seq },
            "compaction_end" => state with { IsCompacting = false, Status = state.IsBusy ? "Busy" : "Idle", LastAppliedSequence = seq },
            "session_info_changed" => ApplySessionName(state, Payload<SessionInfoChangedPayload>(evt.Data), seq),
            // Unknown / unhandled event types: leave state untouched but always advance the watermark.
            _ => state with { LastAppliedSequence = seq },
        };
    }

    private static ClientSessionState ApplyMessage(ClientSessionState state, AgentMessage? message, bool isPending, long seq)
    {
        if (message is null) return state with { LastAppliedSequence = seq };

        var entryId = EntryIdFor(message);
        var row = new ClientTranscriptItem(entryId, message.Role, ExtractText(message), isPending);
        return state with { Transcript = UpsertByEntryId(state.Transcript, row), LastAppliedSequence = seq };
    }

    private static ClientSessionState ApplyToolStart(ClientSessionState state, ToolExecutionStartPayload? payload, long seq)
    {
        if (payload?.ToolCallId is null) return state with { LastAppliedSequence = seq };

        var row = new ClientTranscriptItem(
            EntryId: payload.ToolCallId,
            Role: "tool",
            Text: string.Empty,
            IsPending: true,
            ToolName: payload.ToolName,
            ToolCallId: payload.ToolCallId,
            ToolArguments: JsonText(payload.Arguments),
            IsTool: true);

        return state with
        {
            Transcript = UpsertByToolCallId(state.Transcript, row),
            IsBusy = true,
            Status = "Busy",
            LastAppliedSequence = seq,
        };
    }

    private static ClientSessionState ApplyToolUpdate(ClientSessionState state, ToolExecutionUpdatePayload? payload, long seq)
    {
        if (payload?.ToolCallId is null) return state with { LastAppliedSequence = seq };

        var row = new ClientTranscriptItem(
            EntryId: payload.ToolCallId,
            Role: "tool",
            Text: string.Empty,
            IsPending: true,
            ToolName: payload.ToolName,
            ToolCallId: payload.ToolCallId,
            ToolArguments: JsonText(payload.Arguments),
            IsTool: true);

        return state with
        {
            Transcript = UpsertByToolCallId(state.Transcript, row),
            IsBusy = true,
            Status = "Busy",
            LastAppliedSequence = seq,
        };
    }

    private static ClientSessionState ApplyToolEnd(ClientSessionState state, ToolExecutionEndPayload? payload, long seq)
    {
        if (payload?.ToolCallId is null) return state with { LastAppliedSequence = seq };

        var row = new ClientTranscriptItem(
            EntryId: payload.ToolCallId,
            Role: "tool",
            Text: string.Empty,
            IsPending: false,
            ToolName: payload.ToolName,
            ToolCallId: payload.ToolCallId,
            ToolResult: ObjectText(payload.Result),
            ToolIsError: payload.IsError,
            IsTool: true);

        return state with
        {
            Transcript = UpsertByToolCallId(state.Transcript, row),
            IsBusy = false,
            Status = "Idle",
            LastAppliedSequence = seq,
        };
    }

    private static ClientSessionState ApplyModelSelect(ClientSessionState state, ModelSelectPayload? payload, long seq)
    {
        if (payload?.Model is null) return state with { LastAppliedSequence = seq };

        var model = payload.Model;
        var display = !string.IsNullOrWhiteSpace(model.Name) ? model.Name : model.Id;
        return state with
        {
            ModelDisplay = display,
            ContextWindow = model.ContextWindow > 0 ? model.ContextWindow : state.ContextWindow,
            LastAppliedSequence = seq,
        };
    }

    private static ClientSessionState ApplySessionName(ClientSessionState state, SessionInfoChangedPayload? payload, long seq)
        => state with { SessionName = payload?.Name ?? state.SessionName, LastAppliedSequence = seq };

    // --- transcript upsert helpers ---

    private static IReadOnlyList<ClientTranscriptItem> UpsertByEntryId(
        IReadOnlyList<ClientTranscriptItem> transcript, ClientTranscriptItem row)
    {
        for (var i = 0; i < transcript.Count; i++)
        {
            var existing = transcript[i];
            if (!existing.IsTool && string.Equals(existing.EntryId, row.EntryId, StringComparison.Ordinal))
            {
                var copy = transcript.ToArray();
                copy[i] = row with { ToolName = existing.ToolName, ToolCallId = existing.ToolCallId };
                return copy;
            }
        }

        return Append(transcript, row);
    }

    private static IReadOnlyList<ClientTranscriptItem> UpsertByToolCallId(
        IReadOnlyList<ClientTranscriptItem> transcript, ClientTranscriptItem row)
    {
        for (var i = 0; i < transcript.Count; i++)
        {
            var existing = transcript[i];
            if (existing.IsTool && string.Equals(existing.ToolCallId, row.ToolCallId, StringComparison.Ordinal))
            {
                var copy = transcript.ToArray();
                copy[i] = row with
                {
                    ToolName = string.IsNullOrWhiteSpace(row.ToolName) ? existing.ToolName : row.ToolName,
                    ToolArguments = row.ToolArguments ?? existing.ToolArguments,
                    ToolResult = row.ToolResult ?? existing.ToolResult,
                    ToolIsError = row.ToolIsError || existing.ToolIsError,
                };
                return copy;
            }
        }

        return Append(transcript, row);
    }

    private static IReadOnlyList<ClientTranscriptItem> Append(
        IReadOnlyList<ClientTranscriptItem> transcript, ClientTranscriptItem row)
    {
        var copy = new ClientTranscriptItem[transcript.Count + 1];
        for (var i = 0; i < transcript.Count; i++) copy[i] = transcript[i];
        copy[transcript.Count] = row;
        return copy;
    }

    // --- payload / text helpers ---

    /// <summary>
    /// Deserializes the opaque <see cref="AgentSessionEvent.Data"/> (an anonymous payload object produced
    /// by <c>AgentSessionEvent.FromCore</c>/<c>FromOwn</c>, or a <see cref="JsonElement"/> when the envelope
    /// arrived over the wire) into a typed payload record using the shared agent JSON options.
    /// </summary>
    private static T? Payload<T>(object? data)
    {
        if (data is null) return default;
        return AgentJsonSerializer.Deserialize<T>(AgentJsonSerializer.Serialize(data));
    }

    /// <summary>
    /// Stable identity for a message row. Flat message events carry no entry id, so the reducer derives one
    /// from <see cref="AgentMessage.Role"/> + <see cref="AgentMessage.Timestamp"/>; a streamed assistant
    /// message is mutated with <c>with</c> (preserving its timestamp) across start/update/end, so the three
    /// events fold onto the same row.
    /// </summary>
    private static string EntryIdFor(AgentMessage message) => $"{message.Role}:{message.Timestamp.ToUnixTimeMilliseconds()}";

    private static string ExtractText(AgentMessage message)
    {
        var content = message switch
        {
            UserMessage user => user.Content,
            AssistantMessage assistant => assistant.Content,
            ToolResultMessage toolResult => toolResult.Content,
            _ => null,
        };

        if (content is null) return string.Empty;
        return string.Concat(content.OfType<TextContent>().Select(part => part.Text));
    }


    private static string ObjectText(object? value)
        => value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement json => JsonText(json),
            _ => AgentJsonSerializer.Serialize(value),
        };
    private static string JsonText(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            _ => element.GetRawText(),
        };
    private sealed record SessionInfoChangedPayload(string? Name);
    private sealed record MessagePayload(AgentMessage? Message);
    private sealed record ToolExecutionStartPayload(string ToolCallId, string? ToolName, JsonElement Arguments);
    private sealed record ToolExecutionUpdatePayload(string ToolCallId, string? ToolName, JsonElement Arguments, object? PartialResult);
    private sealed record ToolExecutionEndPayload(string ToolCallId, string? ToolName, object? Result, bool IsError);
    private sealed record ModelSelectPayload(ModelDescriptor? Model, ModelDescriptor? PreviousModel, string? Source);
}
