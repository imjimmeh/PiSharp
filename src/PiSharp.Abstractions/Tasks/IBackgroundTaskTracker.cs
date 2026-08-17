namespace PiSharp.Abstractions.Tasks;

public sealed record BackgroundTaskInfo(
    string Id,
    string Name,
    DateTimeOffset StartedAtUtc,
    bool IsCompleted,
    bool IsFaulted,
    Exception? Exception);

public interface IBackgroundTaskTracker : IAsyncDisposable, IDisposable
{
    bool IsHealthy { get; }
    Exception? LastFault { get; }
    IReadOnlyList<BackgroundTaskInfo> ActiveTasks { get; }
    event Action<string, Exception>? TaskFaulted;

    void Track(string name, Task task, CancellationTokenSource? cts = null);
    Task Run(string name, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
