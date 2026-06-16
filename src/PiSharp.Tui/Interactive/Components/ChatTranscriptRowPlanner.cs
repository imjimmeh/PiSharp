using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class ChatTranscriptRowPlanner
{
    private readonly record struct PlannedTranscriptRowGroup(TuiTranscriptItem Item, IReadOnlyList<TuiChatRow> Rows);

    private List<PlannedTranscriptRowGroup> _transcriptRowGroups = [];
    private int _lastWidth;
    private bool _lastShowThinking;
    private bool _lastShowToolOutput;
    private bool _hasPlan;

    internal IReadOnlyList<TuiChatRow> BuildRows(
        TuiRenderState state,
        int width,
        ChatRowCache cache,
        TuiProfilingCounters? profilingCounters = null)
    {
        var rows = new List<TuiChatRow>(Math.Max(1, state.Transcript.Count * 2 + state.BridgeSlots.Count * 2));
        var activeCacheKeys = cache.ShouldPrune(state) ? new HashSet<ChatRowCache.TranscriptRowCacheKey>() : null;
        var renderOptionsChanged = !_hasPlan
            || _lastWidth != width
            || _lastShowThinking != state.ShowThinking
            || _lastShowToolOutput != state.ShowToolOutput;

        foreach (var slot in state.BridgeSlots.Where(slot => slot.Visible && slot.Placement == "above-chat"))
        {
            cache.AddPaddedRowGroup(rows, ChatView.RenderBridgeSlot(slot, width), width);
        }

        var nextTranscriptRowGroups = new List<PlannedTranscriptRowGroup>(state.Transcript.Count);
        for (var index = 0; index < state.Transcript.Count; index++)
        {
            var item = state.Transcript[index];
            cache.TrackTranscriptItem(item, state, width, activeCacheKeys);
            if (!renderOptionsChanged
                && index < _transcriptRowGroups.Count
                && EqualityComparer<TuiTranscriptItem>.Default.Equals(_transcriptRowGroups[index].Item, item))
            {
                var previousGroup = _transcriptRowGroups[index];
                rows.AddRange(previousGroup.Rows);
                nextTranscriptRowGroups.Add(previousGroup);
                continue;
            }

            profilingCounters?.Increment(TuiProfilingCounterNames.ChatRowGroupPlan);
            var groupRows = new List<TuiChatRow>();
            cache.AddPaddedRowGroup(groupRows, cache.GetOrRenderTranscriptItem(item, state, width, activeCacheKeys), width);
            var plannedGroup = new PlannedTranscriptRowGroup(item, groupRows.ToArray());
            rows.AddRange(plannedGroup.Rows);
            nextTranscriptRowGroups.Add(plannedGroup);
        }

        foreach (var slot in state.BridgeSlots.Where(slot => slot.Visible && slot.Placement != "above-chat"))
        {
            cache.AddPaddedRowGroup(rows, ChatView.RenderBridgeSlot(slot, width), width);
        }

        _transcriptRowGroups = nextTranscriptRowGroups;
        _lastWidth = width;
        _lastShowThinking = state.ShowThinking;
        _lastShowToolOutput = state.ShowToolOutput;
        _hasPlan = true;
        cache.Prune(activeCacheKeys);
        return rows;
    }
}
