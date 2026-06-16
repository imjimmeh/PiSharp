using PiSharp.Abstractions.Sessions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.SessionSelection;

namespace PiSharp.Tui.Interactive.Components;

public static class SessionSelectorDialog
{
    private const int MaxVisible = 10;

    public static async Task<JsonlSessionMetadata?> SelectAsync(
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadCurrentSessionsAsync,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAllSessionsAsync,
        JsonlSessionMetadata? currentSession,
        CancellationToken cancellationToken = default,
        ITuiApplicationContext? appContext = null)
    {
        if (cancellationToken.IsCancellationRequested) return await Task.FromCanceled<JsonlSessionMetadata?>(cancellationToken);
        var currentSessions = await loadCurrentSessionsAsync(cancellationToken).ConfigureAwait(false);

        var ctx = appContext ?? new TerminalGuiApplicationContext();
        var completion = new TaskCompletionSource<JsonlSessionMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        ctx.Post(() =>
        {
            try
            {
                completion.TrySetResult(RunSelector(ctx, currentSessions, loadAllSessionsAsync, currentSession, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await CompleteAndDisposeRegistrationAsync(completion.Task, registration).ConfigureAwait(false);
    }

    public static async Task<JsonlSessionMetadata?> SelectStandaloneAsync(
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadCurrentSessionsAsync,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAllSessionsAsync,
        JsonlSessionMetadata? currentSession,
        CancellationToken cancellationToken = default)
    {
        return await SelectStandaloneInternalAsync(
            loadCurrentSessionsAsync, loadAllSessionsAsync, currentSession,
            new TerminalGuiSessionSelectorRuntime(), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JsonlSessionMetadata?> SelectStandaloneInternalAsync(
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadCurrentSessionsAsync,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAllSessionsAsync,
        JsonlSessionMetadata? currentSession,
        ISessionSelectorRuntime runtime,
        CancellationToken cancellationToken = default,
        ITuiApplicationContext? appContext = null)
    {
        if (cancellationToken.IsCancellationRequested) return await Task.FromCanceled<JsonlSessionMetadata?>(cancellationToken);
        var currentSessions = await loadCurrentSessionsAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            runtime.Enter();
            var ctx = appContext ?? new TerminalGuiApplicationContext();
            return RunSelector(ctx, currentSessions, loadAllSessionsAsync, currentSession, cancellationToken);
        }
        finally
        {
            runtime.Exit();
        }
    }

    private static async Task<JsonlSessionMetadata?> CompleteAndDisposeRegistrationAsync(Task<JsonlSessionMetadata?> selectionTask, CancellationTokenRegistration registration)
    {
        try
        {
            return await selectionTask.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JsonlSessionMetadata? RunSelector(
        ITuiApplicationContext appContext,
        IReadOnlyList<JsonlSessionMetadata> currentSessions,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAllSessionsAsync,
        JsonlSessionMetadata? currentSession,
        CancellationToken cancellationToken)
    {
        _ = currentSession;
        var window = new SessionSelectorWindow(currentSessions, loadAllSessionsAsync, appContext, cancellationToken);
        return window.Show();
    }

    internal static async Task LoadAllSessionsInternalAsync(
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAllSessionsAsync,
        CancellationToken cancellationToken,
        ITuiDispatcher dispatcher,
        Action<IReadOnlyList<JsonlSessionMetadata>> onLoaded)
    {
        try
        {
            var sessions = await loadAllSessionsAsync(cancellationToken).ConfigureAwait(false);
            dispatcher.Post(() => onLoaded(sessions));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            dispatcher.Post(() => onLoaded([]));
        }
    }

    internal static IReadOnlyList<string> RenderWindow(IReadOnlyList<SessionSelectorRow> rows, int selectedIndex, int maxVisible = MaxVisible)
    {
        if (rows.Count == 0) return ["  No sessions found"];
        var startIndex = WindowStartIndex(rows.Count, selectedIndex, maxVisible);
        var endIndex = Math.Min(startIndex + maxVisible, rows.Count);
        var rendered = new List<string>();
        for (var i = startIndex; i < endIndex; i++)
        {
            var row = rows[i];
            var cursor = i == selectedIndex ? "→ " : "  ";
            rendered.Add($"{cursor}{row.TreePrefix}{row.DisplayText}  {row.RightText}");
        }
        if (startIndex > 0 || endIndex < rows.Count) rendered.Add($"  ({selectedIndex + 1}/{rows.Count})");
        return rendered;
    }

    internal static int WindowStartIndex(int itemCount, int selectedIndex, int maxVisible = MaxVisible)
        => itemCount == 0 ? 0 : Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, itemCount - maxVisible));
}
