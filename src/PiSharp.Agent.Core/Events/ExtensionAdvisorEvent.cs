namespace PiSharp.Agent.Core.Events;

public enum ExtensionAdvisorNoteKind { Note, Concern, Blocker, Timeout, Error }

public sealed record ExtensionAdvisorNote(
    string Kind,                 // "note" | "concern" | "blocker" | "timeout" | "error"
    string Text,                 // the advisor's note
    string? ToolName = null,     // optional tool/area the note concerns
    string? Model = null,        // advisor model id that produced it
    DateTimeOffset Timestamp = default);

public sealed record ExtensionAdvisorEvent(
    string SessionId,
    string TurnId,               // id of the reviewed turn (assistant message id) — may be empty
    ExtensionAdvisorNote Note);
