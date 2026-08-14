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
    Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, CancellationToken ct);
    ChannelReader<ServerEventEnvelope> Events { get; }

    /// <summary>
    /// Sends a command with an optional payload object whose properties are merged into the frame.
    /// The daemon deserializes each frame into a flat request record (envelope fields plus payload
    /// fields, e.g. <c>attach</c>'s <c>sinceSequence</c>), so the payload rides in the same object.
    /// Transports without payload support ignore the payload and send the envelope alone.
    /// </summary>
    Task<ServerResponse> SendCommandAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct)
        => SendCommandAsync(envelope, ct);
}
