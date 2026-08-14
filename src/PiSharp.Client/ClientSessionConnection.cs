using System.Threading.Channels;
using PiSharp.Server.Contracts;

namespace PiSharp.Client;

/// <summary>
/// High-level connection to a daemon session: assigns command ids when omitted, forwards commands to
/// the <see cref="IClientTransport"/>, and pumps the transport's event stream into
/// <see cref="EventReceived"/> while tracking <see cref="LastAppliedSequence"/>.
/// </summary>
public sealed class ClientSessionConnection : IAsyncDisposable
{
    private readonly IClientTransport _transport;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;

    public ClientSessionConnection(IClientTransport transport)
    {
        _transport = transport;
        _pumpTask = Task.Run(() => PumpEventsAsync(_pumpCts.Token), CancellationToken.None);
    }

    /// <summary>Raised on the event-pump task for every envelope read from <see cref="IClientTransport.Events"/>.</summary>
    public event Action<ServerEventEnvelope>? EventReceived;

    /// <summary>Sequence watermark of the most recent envelope raised via <see cref="EventReceived"/>.</summary>
    public long LastAppliedSequence { get; private set; }

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct = default)
        => _transport.ConnectAsync(uri, apiKey, ct);

    /// <summary>
    /// Sends a command, assigning a fresh <see cref="ServerCommandEnvelope.Id"/> when the caller
    /// omitted one, and returns the server's response.
    /// </summary>
    public async Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope.Id is null)
        {
            envelope = envelope with { Id = Guid.NewGuid().ToString("N") };
        }
        return await _transport.SendCommandAsync(envelope, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _pumpCts.Cancel();
        try
        {
            await _pumpTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pumpCts.Dispose();
            await _transport.DisposeAsync();
        }
    }

    private async Task PumpEventsAsync(CancellationToken ct)
    {
        await foreach (var envelope in _transport.Events.ReadAllAsync(ct))
        {
            LastAppliedSequence = envelope.Sequence;
            try
            {
                EventReceived?.Invoke(envelope);
            }
            catch (Exception)
            {
                // A misbehaving subscriber must not stall the event stream.
            }
        }
    }
}
