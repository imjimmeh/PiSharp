namespace PiSharp.Tui.Interactive;

public static class TuiTranscriptReducer
{
    public static TuiRenderState AppendSystem(
        TuiRenderState state,
        string text,
        bool isError = false,
        bool pinToTop = false,
        TimeSpan? expiresAfter = null,
        string? localId = null,
        string? systemMessageTag = null,
        TimeSpan? removeDelayAfterEvent = null)
    {
        var item = new TuiTranscriptItem(
            "system",
            text,
            IsError: isError,
            IsPinnedToTop: pinToTop,
            ExpiresAt: expiresAfter is null ? null : DateTimeOffset.UtcNow.Add(expiresAfter.Value),
            LocalId: localId,
            SystemMessageTag: systemMessageTag,
            RemoveDelayAfterEvent: removeDelayAfterEvent);
        return pinToTop ? AppendPinned(state, item) : Append(state, item);
    }

    public static TuiRenderState RemoveLocalSystemRow(TuiRenderState state, string localId)
        => state with { Transcript = state.Transcript.Where(item => !IsLocalSystemRow(item) || !string.Equals(item.LocalId, localId, StringComparison.Ordinal)).ToArray() };

    public static TuiRenderState RemoveExpiredSystemRows(TuiRenderState state, DateTimeOffset now)
    {
        if (!state.Transcript.Any(item => IsExpiredLocalSystemRow(item, now)))
            return state;
        return state with { Transcript = state.Transcript.Where(item => !IsExpiredLocalSystemRow(item, now)).ToArray() };
    }

    public static TuiRenderState TriggerSystemMessageEvent(TuiRenderState state, string eventTag, DateTimeOffset now)
        => state with
        {
            Transcript = state.Transcript.Select(item =>
            {
                if (!IsLocalSystemRow(item) || !string.Equals(item.SystemMessageTag, eventTag, StringComparison.Ordinal))
                    return item;
                var delay = item.RemoveDelayAfterEvent ?? TimeSpan.Zero;
                return item with { ExpiresAt = now.Add(delay) };
            }).ToArray()
        };

    public static TuiRenderState RestoreLocalSystemRows(TuiRenderState state, IEnumerable<TuiTranscriptItem> rows)
    {
        var localRows = rows.Where(IsLocalSystemRow).ToArray();
        if (localRows.Length == 0) return state;

        var pinnedRows = localRows.Where(row => row.IsPinnedToTop).ToArray();
        var unpinnedRows = localRows.Where(row => !row.IsPinnedToTop).ToArray();
        return state with { Transcript = pinnedRows.Concat(state.Transcript).Concat(unpinnedRows).ToArray() };
    }

    public static TuiRenderState ClearTranscript(TuiRenderState state)
        => state with { Transcript = [] };

    public static TuiTranscriptItem? FindTranscriptItemByEntryId(TuiRenderState state, string entryId)
        => state.Transcript.LastOrDefault(item => string.Equals(item.EntryId, entryId, StringComparison.Ordinal));

    private static TuiRenderState Append(TuiRenderState state, TuiTranscriptItem item)
        => state with { Transcript = state.Transcript.Concat([item]).ToArray() };

    private static TuiRenderState AppendPinned(TuiRenderState state, TuiTranscriptItem item)
    {
        var copy = state.Transcript.ToList();
        var insertIndex = copy.TakeWhile(existing => existing.IsPinnedToTop).Count();
        copy.Insert(insertIndex, item);
        return state with { Transcript = copy };
    }

    private static bool IsLocalSystemRow(TuiTranscriptItem item)
        => item.EntryId is null && string.Equals(item.Role, "system", StringComparison.Ordinal);

    private static bool IsExpiredLocalSystemRow(TuiTranscriptItem item, DateTimeOffset now)
        => IsLocalSystemRow(item) && item.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= now;
}
