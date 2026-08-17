using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.Abstractions.Tasks;

public sealed class BackgroundTaskTracker : IBackgroundTaskTracker
{
    private sealed record TrackedTaskEntry(
        string Id,
        string Name,
        DateTimeOffset StartedAtUtc,
        Task Task,
        CancellationTokenSource? Cts);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TrackedTaskEntry> _tasks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownCts = new();
    private Exception? _lastFault;
    private int _disposed;

    public BackgroundTaskTracker(ILogger? logger = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = logger ?? loggerFactory?.CreateLogger<BackgroundTaskTracker>() ?? NullLogger<BackgroundTaskTracker>.Instance;
    }

    public bool IsHealthy => _lastFault is null;
    public Exception? LastFault => _lastFault;

    public IReadOnlyList<BackgroundTaskInfo> ActiveTasks
        => _tasks.Values.Select(entry => new BackgroundTaskInfo(
            entry.Id,
            entry.Name,
            entry.StartedAtUtc,
            entry.Task.IsCompleted,
            entry.Task.IsFaulted,
            entry.Task.Exception?.GetBaseException())).ToArray();

    public event Action<string, Exception>? TaskFaulted;

    public void Track(string name, Task task, CancellationTokenSource? cts = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new TrackedTaskEntry(id, name, DateTimeOffset.UtcNow, task, cts);
        _tasks[id] = entry;

        _ = task.ContinueWith(t =>
        {
            _tasks.TryRemove(id, out _);
            if (t.IsFaulted && t.Exception is not null)
            {
                var baseEx = t.Exception.GetBaseException();
                _lastFault = baseEx;
                _logger.LogError(baseEx, "Background task '{TaskName}' ({TaskId}) faulted unexpectedly", name, id);
                TaskFaulted?.Invoke(name, baseEx);
            }
            else if (t.IsCanceled)
            {
                _logger.LogDebug("Background task '{TaskName}' ({TaskId}) cancelled cleanly", name, id);
            }
            else
            {
                _logger.LogDebug("Background task '{TaskName}' ({TaskId}) completed cleanly", name, id);
            }
        }, TaskScheduler.Default);
    }

    public Task Run(string name, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        var task = Task.Run(async () =>
        {
            try
            {
                await action(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                cts.Dispose();
            }
        }, CancellationToken.None);

        Track(name, task, cts);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdownCts.Cancel();
        var pending = _tasks.Values.Select(entry => entry.Task).ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "One or more background tasks faulted or timed out during shutdown");
            }
        }
        _shutdownCts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
