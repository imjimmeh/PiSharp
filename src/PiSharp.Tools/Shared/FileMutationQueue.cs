using PiSharp.Abstractions.Environment;

namespace PiSharp.Tools.Shared;

public static class FileMutationQueue
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task> Queues = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> RunAsync<T>(IFileSystem fileSystem, string path, Func<Task<T>> operation)
    {
        var key = await GetQueueKeyAsync(fileSystem, path).ConfigureAwait(false);
        Task previous;
        Task chained;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            previous = Queues.GetValueOrDefault(key) ?? Task.CompletedTask;
            chained = previous.ContinueWith(_ => release.Task, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            Queues[key] = chained;
        }

        await previous.ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            release.SetResult();
            lock (Gate)
            {
                if (ReferenceEquals(Queues.GetValueOrDefault(key), chained)) Queues.Remove(key);
            }
        }
    }

    private static async Task<string> GetQueueKeyAsync(IFileSystem fileSystem, string path)
    {
        var absolute = await PathUtilities.ResolvePathAsync(fileSystem, path).ConfigureAwait(false);
        var exists = await fileSystem.ExistsAsync(absolute).ConfigureAwait(false);
        if (exists.IsOk && exists.Value)
        {
            var canonical = await fileSystem.GetCanonicalPathAsync(absolute).ConfigureAwait(false);
            if (canonical.IsOk) return canonical.Value;
        }
        return absolute;
    }
}
