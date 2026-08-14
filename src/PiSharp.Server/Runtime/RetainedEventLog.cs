using PiSharp.Server.Contracts;

namespace PiSharp.Server.Runtime;

/// <summary>Outcome of a sequence replay against the retained event log.</summary>
public sealed record ReplayResult(long FromSequence, long HeadSequence, bool Gap, IReadOnlyList<ServerEventEnvelope> Events);

/// <summary>
/// Lock-guarded ring buffer of the most recent <c>capacity</c> session envelopes, replayable by the
/// per-session monotonic <see cref="ServerEventEnvelope.Sequence"/>. A replay reports a gap only when
/// the requested <c>sinceSequence</c> falls inside the evicted range — between the first sequence ever
/// appended and the oldest retained envelope. Requests at or before the very first sequence (e.g. a
/// fresh client attaching at 0) replay everything retained.
/// </summary>
public sealed class RetainedEventLog(int capacity)
{
    private readonly object _gate = new();
    private readonly ServerEventEnvelope[] _buffer = new ServerEventEnvelope[capacity];
    private int _start;   // index of the oldest retained envelope
    private int _count;
    private long _head;
    private long _firstSequence = -1;
    public long HeadSequence { get { lock (_gate) return _head; } }

    public void Append(ServerEventEnvelope envelope)
    {
        lock (_gate)
        {
            if (_count == 0) _firstSequence = envelope.Sequence;
            if (_count < capacity) { _buffer[(_start + _count) % capacity] = envelope; _count++; }
            else { _buffer[_start] = envelope; _start = (_start + 1) % capacity; }
            _head = envelope.Sequence;
        }
    }

    public ReplayResult ReplayFrom(long sinceSequence)
    {
        lock (_gate)
        {
            var oldest = _head - _count + 1;
            var gap = _count > 0 && sinceSequence >= _firstSequence && sinceSequence < oldest;
            // Clamp the reported FromSequence to the oldest retained sequence when the request
            // falls inside the evicted range; fresh attaches at/before the first sequence stay
            // unclamped and gap-free (the client replays everything retained).
            var fromSequence = gap ? oldest : sinceSequence;
            var events = new List<ServerEventEnvelope>();
            for (var i = 0; i < _count; i++)
            {
                var envelope = _buffer[(_start + i) % capacity];
                if (envelope.Sequence >= sinceSequence) events.Add(envelope);
            }
            return new ReplayResult(fromSequence, _head, gap, events);
        }
    }
}
