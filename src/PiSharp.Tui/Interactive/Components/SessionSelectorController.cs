using PiSharp.Abstractions.Sessions;
using PiSharp.Tui.Interactive.SessionSelection;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class SessionSelectorController
{
    public SessionSelectorScope Scope { get; set; } = SessionSelectorScope.Current;
    public IReadOnlyList<JsonlSessionMetadata>? AllSessions { get; set; }
    public bool IsActive { get; set; } = true;
    public Task? LoadingTask { get; private set; }
    public Action? OnSessionsLoaded { get; set; }

    public bool TryStartLoading(
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>? loadAllSessionsAsync,
        CancellationToken cancellationToken,
        ITuiDispatcher dispatcher,
        out string loadingTitle,
        out IReadOnlyList<string> loadingRows)
    {
        loadingTitle = string.Empty;
        loadingRows = [];

        if (Scope != SessionSelectorScope.All || AllSessions is not null)
            return false;

        if (LoadingTask?.IsCompleted == false)
            return false;

        if (loadAllSessionsAsync is null)
        {
            loadingTitle = "Resume Session (All)";
            loadingRows = [];
            return false;
        }

        loadingTitle = "Resume Session (Loading All)";
        loadingRows = ["  Loading sessions..."];

        LoadingTask = SessionSelectorDialog.LoadAllSessionsInternalAsync(
            loadAllSessionsAsync, cancellationToken, dispatcher, OnLoadCompleted);

        return true;
    }

    private void OnLoadCompleted(IReadOnlyList<JsonlSessionMetadata> loadedSessions)
    {
        if (!IsActive) return;
        AllSessions = loadedSessions;
        OnSessionsLoaded?.Invoke();
    }
}
