namespace PiSharp.Abstractions.Streaming;

/// <summary>
/// Push/result async stream matching the TypeScript EventStream pattern.
/// </summary>
public interface IEventStream<TEvent, TResult> : IAsyncEnumerable<TEvent>
{
    void Push(TEvent @event);

    void End(TResult result);

    Task<TResult> Result { get; }
}
