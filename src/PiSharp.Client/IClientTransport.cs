using System.Threading.Channels;
using PiSharp.Server.Contracts;

namespace PiSharp.Client;

/// <summary>
/// Transport abstraction for the daemon WebSocket protocol: command/response correlation plus a
/// sequence-ordered stream of <see cref="ServerEventEnvelope"/>s.
/// </summary>
public interface IClientTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct);

    /// <summary>
    /// Sends a command and waits for its correlated response. When <paramref name="timeoutOverride"/>
    /// is provided it replaces the transport's default command timeout for this call only; other
    /// commands keep the default.
    /// </summary>
    Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct, TimeSpan? timeoutOverride = null);

    /// <summary>
    /// Responses that arrived after their command already timed out client-side. Timed-out
    /// responses must be observed, not dropped: a late <c>run_command</c> <c>ShouldExit</c> or a
    /// session-creation result would otherwise be lost. Bounded — when the lane is full, further
    /// late responses are discarded.
    /// </summary>
    ChannelReader<ServerResponse> LateResponses { get; }
    ChannelReader<ServerEventEnvelope> Events { get; }

    /// <summary>
    /// Sends a command with an optional payload object whose properties are merged into the frame.
    /// The daemon deserializes each frame into a flat request record (envelope fields plus payload
    /// fields, e.g. <c>attach</c>'s <c>sinceSequence</c>), so the payload rides in the same object.
    /// Transports without payload support ignore the payload and send the envelope alone.
    /// </summary>
    Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct, TimeSpan? timeoutOverride = null)
        => SendCommandAsync(envelope, ct, timeoutOverride);
}
