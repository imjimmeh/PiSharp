using PiSharp.Agent.Core.Events;
using System.Threading.Channels;

namespace PiSharp.Tui.Interactive.Harness;

internal enum TuiHarnessEventEnqueueResult
{
    NotQueued,
    QueuedImmediately,
    QueuedAsynchronously
}

internal sealed record QueuedHarnessEvent(AgentHarnessEvent Event, CancellationToken CancellationToken);

internal sealed class TuiHarnessEventPump : IDisposable
{
    private readonly Action<IReadOnlyList<QueuedHarnessEvent>> _dispatchBatch;
    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _batchInterval;
    private readonly int _batchSize;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Channel<QueuedHarnessEvent> _queue;
    private readonly Task _worker;

    public TuiHarnessEventPump(
        Action<IReadOnlyList<QueuedHarnessEvent>> dispatchBatch,
        Action<Action> dispatch,
        TimeSpan batchInterval,
        int capacity = 4096,
        int batchSize = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        _dispatchBatch = dispatchBatch;
        _dispatch = dispatch;
        _batchInterval = batchInterval;
        _batchSize = batchSize;
        _queue = Channel.CreateBounded<QueuedHarnessEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);
    }

    public TuiHarnessEventEnqueueResult Enqueue(AgentHarnessEvent evt, CancellationToken token)
    {
        if (_cancellation.IsCancellationRequested) return TuiHarnessEventEnqueueResult.NotQueued;

        var queued = new QueuedHarnessEvent(evt, token);
        if (_queue.Writer.TryWrite(queued)) return TuiHarnessEventEnqueueResult.QueuedImmediately;

        _ = WriteAsync(queued, _cancellation.Token);
        return TuiHarnessEventEnqueueResult.QueuedAsynchronously;
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cancellation.Cancel();
        if (!_worker.IsCompleted)
        {
            try
            {
                _worker.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
            }
        }

        _cancellation.Dispose();
    }

    private async Task WriteAsync(QueuedHarnessEvent evt, CancellationToken token)
    {
        try
        {
            await _queue.Writer.WriteAsync(evt, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var batch = new List<QueuedHarnessEvent>(_batchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(token))
            {
                batch.Clear();
                while (batch.Count < _batchSize && _queue.Reader.TryRead(out var evt)) batch.Add(evt);
                if (batch.Count == 0) continue;

                if (batch.Count < _batchSize)
                {
                    var delay = Task.Delay(_batchInterval, token);
                    while (batch.Count < _batchSize)
                    {
                        while (batch.Count < _batchSize && _queue.Reader.TryRead(out var evt)) batch.Add(evt);
                        if (batch.Count >= _batchSize) break;

                        var canRead = _queue.Reader.WaitToReadAsync(token).AsTask();
                        var completed = await Task.WhenAny(delay, canRead);
                        if (completed == delay) break;
                        if (!await canRead) break;
                    }
                }

                var currentBatch = batch.ToArray();
                _dispatch(() => _dispatchBatch(currentBatch));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
