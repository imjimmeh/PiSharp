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
