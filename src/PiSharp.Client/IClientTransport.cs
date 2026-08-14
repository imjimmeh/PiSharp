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
}
