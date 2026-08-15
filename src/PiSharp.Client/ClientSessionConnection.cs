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
    private int _disposed;

    public ClientSessionConnection(IClientTransport transport)
    {
        _transport = transport;
        _pumpTask = Task.Run(() => PumpEventsAsync(_pumpCts.Token), CancellationToken.None);
    }

    /// <summary>Raised on the event-pump task for every envelope read from <see cref="IClientTransport.Events"/>.</summary>
    public event Action<ServerEventEnvelope>? EventReceived;

    /// <summary>Sequence watermark of the most recent envelope raised via <see cref="EventReceived"/>.</summary>
    public long LastAppliedSequence { get; private set; }

    /// <summary>Responses that arrived after their command timed out (see <see cref="IClientTransport.LateResponses"/>).</summary>
    public ChannelReader<ServerResponse> LateResponses => _transport.LateResponses;

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct = default)
        => _transport.ConnectAsync(uri, apiKey, ct);

    /// <summary>
    /// Sends a command, assigning a fresh <see cref="ServerCommandEnvelope.Id"/> when the caller
    /// omitted one, and returns the server's response.
    /// </summary>
    public Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, CancellationToken ct = default, TimeSpan? timeoutOverride = null)
        => SendAsync(envelope, payload: null, ct, timeoutOverride);

    /// <summary>
    /// Sends a command, assigning a fresh <see cref="ServerCommandEnvelope.Id"/> when the caller
    /// omitted one, and returns the server's response. The optional <paramref name="payload"/> object's
    /// properties are merged into the frame (see <see cref="IClientTransport.SendCommandAsync(ServerCommandEnvelope, object?, CancellationToken)"/>).
    /// When <paramref name="timeoutOverride"/> is provided it replaces the transport's default command
    /// timeout for this call only.
    /// </summary>
    public async Task<ServerResponse> SendAsync(ServerCommandEnvelope envelope, object? payload, CancellationToken ct = default, TimeSpan? timeoutOverride = null)
    {
        if (envelope.Id is null)
        {
            envelope = envelope with { Id = Guid.NewGuid().ToString("N") };
        }
        return await _transport.SendCommandAsync(envelope, payload, ct, timeoutOverride);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
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
