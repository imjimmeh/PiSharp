using PiSharp.Agent.Core.Events;

namespace PiSharp.Sdk;

/// <summary>
/// Raised by <see cref="SessionConnection.Changed"/> after a batch of envelopes has been applied to
/// the client-side session state. The batch is normally a single event per frame on the wire; the
/// args carry the applied events plus the sequence watermarks for gap detection.
/// </summary>
public sealed record ClientSessionChangedEventArgs(
    IReadOnlyList<AgentSessionEvent> Applied,
    long LastAppliedSequence,
    long HeadSequence);
