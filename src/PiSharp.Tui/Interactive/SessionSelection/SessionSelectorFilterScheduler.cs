using PiSharp.Abstractions.Sessions;

namespace PiSharp.Tui.Interactive.SessionSelection;

internal sealed class SessionSelectorFilterScheduler : IDisposable
{
    internal delegate Task<SessionSelectorRow[]> BuildRowsAsync(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string query,
        bool showCwd,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    private readonly Action<Action> _post;
    private readonly BuildRowsAsync _buildRowsAsync;
    private readonly TimeSpan _debounceDelay;
    private CancellationTokenSource? _pendingCancellation;
    private int _version;

    internal SessionSelectorFilterScheduler(Action<Action> post)
        : this(post, BuildRowsOnBackgroundThreadAsync, TimeSpan.FromMilliseconds(25))
    {
    }

    internal SessionSelectorFilterScheduler(Action<Action> post, BuildRowsAsync buildRowsAsync, TimeSpan debounceDelay)
    {
        _post = post;
        _buildRowsAsync = buildRowsAsync;
        _debounceDelay = debounceDelay;
    }

    internal void Schedule(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string query,
        bool showCwd,
        DateTimeOffset now,
        Action<SessionSelectorRow[]> applyRows,
        CancellationToken cancellationToken)
    {
        CancelPending();
        var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pendingCancellation = pendingCancellation;
        var version = unchecked(++_version);
        _ = RunAsync(sessions, query, showCwd, now, applyRows, version, pendingCancellation);
    }

    internal void CancelPending()
    {
        unchecked
        {
            _version++;
        }

        if (_pendingCancellation is null) return;

        _pendingCancellation.Cancel();
        _pendingCancellation = null;
    }

    public void Dispose() => CancelPending();

    private async Task RunAsync(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string query,
        bool showCwd,
        DateTimeOffset now,
        Action<SessionSelectorRow[]> applyRows,
        int version,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            if (_debounceDelay > TimeSpan.Zero) await Task.Delay(_debounceDelay, cancellationToken).ConfigureAwait(false);
            var rows = await _buildRowsAsync(sessions, query, showCwd, now, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || version != _version) return;

            _post(() =>
            {
                if (cancellationToken.IsCancellationRequested || version != _version) return;
                applyRows(rows);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_pendingCancellation, cancellation)) _pendingCancellation = null;
            cancellation.Dispose();
        }
    }

    private static Task<SessionSelectorRow[]> BuildRowsOnBackgroundThreadAsync(
        IReadOnlyList<JsonlSessionMetadata> sessions,
        string query,
        bool showCwd,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => Task.Run(
            () => SessionSelectorModel.BuildRows(sessions, query, showCwd: showCwd, now: now).ToArray(),
            cancellationToken);
}
