namespace PiSharp.Tui.Interactive;

public static class TuiDispatcherExtensions
{
    public static Task InvokeAsync(this ITuiDispatcher dispatcher, Action action, CancellationToken cancellationToken = default)
        => InvokeAsync(dispatcher.Post, action, cancellationToken);

    public static Task<T> InvokeAsync<T>(this ITuiDispatcher dispatcher, Func<T> action, CancellationToken cancellationToken = default)
        => InvokeAsync(dispatcher.Post, action, cancellationToken);

    internal static Task InvokeAsync(Action<Action> post, Action action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        CancellationTokenRegistration cancellationRegistration = default;
        if (cancellationToken.CanBeCanceled)
            cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            post(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        return;
                    }

                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            cancellationRegistration.Dispose();
            return Task.FromException(ex);
        }

        return cancellationToken.CanBeCanceled
            ? DisposeRegistrationOnCompletionAsync(completion.Task, cancellationRegistration)
            : completion.Task;
    }

    internal static Task<T> InvokeAsync<T>(Action<Action> post, Func<T> action, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        CancellationTokenRegistration cancellationRegistration = default;
        if (cancellationToken.CanBeCanceled)
            cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            post(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        return;
                    }

                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            cancellationRegistration.Dispose();
            return Task.FromException<T>(ex);
        }

        return cancellationToken.CanBeCanceled
            ? DisposeRegistrationOnCompletionAsync(completion.Task, cancellationRegistration)
            : completion.Task;
    }

    private static async Task DisposeRegistrationOnCompletionAsync(Task task, CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    private static async Task<T> DisposeRegistrationOnCompletionAsync<T>(Task<T> task, CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }
}
