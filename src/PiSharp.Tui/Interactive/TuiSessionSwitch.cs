namespace PiSharp.Tui.Interactive;

public static class TuiSessionSwitch
{
    private const string LoadingRowId = "session-switch-loading";

    public static TuiRenderState ApplySnapshot(TuiRenderState state, TuiSessionSnapshot snapshot, bool preserveLocalSystemRows)
    {
        var localSystemRows = preserveLocalSystemRows
            ? state.Transcript.Where(item => item.EntryId is null && string.Equals(item.Role, "system", StringComparison.Ordinal) && item.ExpiresAt is null).ToArray()
            : [];
        var hydrated = state.HydrateSession(snapshot.SessionId, snapshot.SessionFile, snapshot.SessionName, snapshot.BranchEntries, snapshot.Model, snapshot.ThinkingLevel);
        if (snapshot.FooterSnapshot is not null)
            hydrated = hydrated with { FooterSnapshot = snapshot.FooterSnapshot };
        if (snapshot.ModifiedFiles is not null)
            hydrated = hydrated with { ModifiedFiles = snapshot.ModifiedFiles };
        if (snapshot.ExtensionLoadStatus is not null)
            hydrated = hydrated with { ExtensionLoadStatus = snapshot.ExtensionLoadStatus };
        if (snapshot.Shortcuts is not null)
            hydrated = hydrated with { ExtensionShortcuts = snapshot.Shortcuts };
        if (snapshot.Commands is not null)
            hydrated = hydrated with { AvailableCommands = snapshot.Commands };
        return localSystemRows.Length == 0 ? hydrated : hydrated.RestoreLocalSystemRows(localSystemRows);
    }

    public static TuiRenderState BeginResumeLoading(TuiRenderState state)
        => (state with { IsBusy = true, Status = "Loading sessions", WorkingMessage = "Loading sessions..." })
            .AppendSystem("Loading sessions... Press Esc in the selector to cancel.",
                localId: LoadingRowId,
                systemMessageTag: "session-load",
                expiresAfter: TimeSpan.FromSeconds(15));

    public static TuiRenderState EndCommandLoading(TuiRenderState state)
        => state
            .TriggerSystemMessageEvent("session-load", DateTimeOffset.UtcNow)
            .RemoveLocalSystemRow(LoadingRowId) with
        { IsBusy = false, Status = "Idle", WorkingMessage = null };
}
