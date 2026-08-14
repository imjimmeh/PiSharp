using PiSharp.Abstractions.Messages;

namespace PiSharp.Agent.Core.Streaming;

/// <summary>
/// Action a stream-delta interceptor can request for the in-flight provider stream.
/// </summary>
public enum StreamDeltaAction
{
    /// <summary>Keep streaming; the delta is applied as usual.</summary>
    Continue,

    /// <summary>Cancel the in-flight attempt and end the turn with a provider-style error message.</summary>
    Abort,

    /// <summary>Cancel the in-flight attempt, discard the partial, inject a system reminder, and retry the request.</summary>
    Retry
}

/// <summary>
/// The interceptor's verdict for a single stream delta. A null decision from
/// every interceptor means <see cref="StreamDeltaAction.Continue"/>.
/// </summary>
public sealed record StreamDeltaDecision(
    StreamDeltaAction Action,
    string? SystemReminder = null,   // rule content to inject as a UserMessage when Retry
    string? Reason = null)           // e.g. "rule:<name>" — carried into auto_retry_start.errorMessage
{
    public static readonly StreamDeltaDecision Continue = new(StreamDeltaAction.Continue);
}

/// <summary>
/// Snapshot of the in-flight stream at the moment an interceptor observes a delta.
/// </summary>
public sealed record StreamDeltaContext(
    AssistantMessageEvent Delta,     // the raw delta event (TextDelta/ThinkingDelta/...)
    AssistantMessage Partial,        // partial message BEFORE this delta is applied
    AgentContext Context);           // current loop context (Messages snapshot)
