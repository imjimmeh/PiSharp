using PiSharp.Tui.Interactive.Rendering;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class ChatRowCache
{
    internal readonly record struct TranscriptRowCacheKey(TuiTranscriptItem Item, int Width, bool ShowThinking, bool ShowToolOutput);

    private const int MinimumCacheEntriesBeforePrune = 1_000;
    private const int ChatRowHorizontalPadding = 2;
    private const int ChatRowVerticalPadding = 1;

    private readonly Dictionary<TranscriptRowCacheKey, IReadOnlyList<TuiChatRow>> _transcriptRowCache = [];
    private readonly Dictionary<int, TuiChatRow> _separatorRowCache = [];

    internal Func<TuiTranscriptItem, TuiRenderState, int, IReadOnlyList<TuiChatRow>> Renderer { get; set; }

    internal ChatRowCache(Func<TuiTranscriptItem, TuiRenderState, int, IReadOnlyList<TuiChatRow>> renderer)
    {
        Renderer = renderer;
    }

    internal static int ContentWidth(int width)
        => Math.Max(1, width - ChatRowHorizontalPadding * 2);

    internal static TuiChatRow ApplyHorizontalPadding(TuiChatRow row, int width)
    {
        if (width <= 0) return row;

        var leftPadding = Math.Min(ChatRowHorizontalPadding, Math.Max(0, width));
        var rightPadding = Math.Min(ChatRowHorizontalPadding, Math.Max(0, width - leftPadding));
        var contentWidth = Math.Max(0, width - leftPadding - rightPadding);
        var content = row.PadTo(contentWidth);
        var left = new string(' ', leftPadding);
        var right = new string(' ', rightPadding);
        var text = left + content.Text + right;
        var spans = content.Spans is { Count: > 0 }
            ? new[] { new TuiSpan(left) }.Concat(content.Spans).Concat([new TuiSpan(right)]).ToArray()
            : null;
        return content with { Text = text, Spans = spans };
    }

    internal IReadOnlyList<TuiChatRow> GetOrRenderTranscriptItem(TuiTranscriptItem item, TuiRenderState state, int width, HashSet<TranscriptRowCacheKey>? activeCacheKeys)
    {
        var key = new TranscriptRowCacheKey(item, width, state.ShowThinking, state.ShowToolOutput);
        activeCacheKeys?.Add(key);
        if (_transcriptRowCache.TryGetValue(key, out var cached)) return cached;

        var contextTarget = MessageContextTarget(item);
        var contentWidth = ContentWidth(width);
        var rows = Renderer(item, state, contentWidth)
            .Select(row => contextTarget is null || row.ContextTarget is not null ? row : row with { ContextTarget = contextTarget })
            .Select(row => ApplyHorizontalPadding(row, width))
            .ToArray();
        _transcriptRowCache[key] = rows;
        return rows;
    }

    internal void TrackTranscriptItem(TuiTranscriptItem item, TuiRenderState state, int width, HashSet<TranscriptRowCacheKey>? activeCacheKeys)
        => activeCacheKeys?.Add(new TranscriptRowCacheKey(item, width, state.ShowThinking, state.ShowToolOutput));

    internal TuiChatRow GetSeparatorRow(int width)
    {
        if (_separatorRowCache.TryGetValue(width, out var row)) return row;

        row = new TuiChatRow(string.Empty).PadTo(width);
        _separatorRowCache[width] = row;
        return row;
    }

    internal void AddPaddedRowGroup(List<TuiChatRow> rows, IReadOnlyList<TuiChatRow> group, int width)
    {
        if (group.Count == 0) return;

        rows.AddRange(group);
        for (var i = 0; i < ChatRowVerticalPadding; i++) rows.Add(GetSeparatorRow(width));
    }

    internal bool ShouldPrune(TuiRenderState state)
    {
        var activeTranscriptKeys = Math.Max(1, state.Transcript.Count);
        var pruneThreshold = Math.Max(MinimumCacheEntriesBeforePrune, activeTranscriptKeys * 2);
        return _transcriptRowCache.Count > pruneThreshold;
    }

    internal void Prune(HashSet<TranscriptRowCacheKey>? activeCacheKeys)
    {
        if (activeCacheKeys is null) return;

        foreach (var key in _transcriptRowCache.Keys.Where(key => !activeCacheKeys.Contains(key)).ToArray())
        {
            _transcriptRowCache.Remove(key);
        }
    }

    internal static TuiInteractionTarget? MessageContextTarget(TuiTranscriptItem item)
    {
        if (string.IsNullOrWhiteSpace(item.EntryId)) return null;
        return new TuiInteractionTarget(
            "message",
            item.EntryId,
            Action: "context",
            Data: new Dictionary<string, string> { ["role"] = item.Role });
    }
}
