using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PiSharp.Abstractions.Streaming;

namespace PiSharp.Agent.Streaming;

public sealed class EventStream<TEvent, TResult> : IEventStream<TEvent, TResult>
{
    private readonly Channel<TEvent> _channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _ended;

    public Task<TResult> Result => _result.Task;

    public void Push(TEvent @event)
    {
        if (Volatile.Read(ref _ended) == 1) return;
        _channel.Writer.TryWrite(@event);
    }

    public void End(TResult result)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        _result.TrySetResult(result);
        _channel.Writer.TryComplete();
    }

    public void Error(Exception exception)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        _result.TrySetException(exception);
        _channel.Writer.TryComplete(exception);
    }

    public async IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }
}
