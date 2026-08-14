namespace PiSharp.Client;

/// <summary>
/// A single row in the client transcript: either a message row (keyed by <see cref="EntryId"/>)
/// or a tool row (keyed by <see cref="ToolCallId"/>, <see cref="IsTool"/> = true).
/// </summary>
public sealed record ClientTranscriptItem(
    string EntryId,
    string Role,
    string Text,
    bool IsPending,
    string? ToolName = null,
    string? ToolCallId = null,
    string? ToolResult = null,
    bool ToolIsError = false,
    bool IsTool = false,
    string? ToolArguments = null);

/// <summary>
/// Client-side, event-sourced snapshot of a single daemon session. Mutated only by
/// <see cref="ClientEventReducer.Apply"/>; never holds I/O or wire state.
/// </summary>
public sealed record ClientSessionState(
    IReadOnlyList<ClientTranscriptItem> Transcript,
    bool IsBusy,
    bool IsCompacting,
    string Status,
    string? ModelDisplay,
    long? ContextWindow,
    string ThinkingLevel,
    long LastAppliedSequence,
    string? SessionName)
{
    public static ClientSessionState Empty { get; } = new([], false, false, "Idle", null, null, "none", 0, null);
}
