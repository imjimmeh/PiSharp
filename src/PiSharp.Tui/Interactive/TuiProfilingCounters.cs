using System.Collections.Concurrent;

namespace PiSharp.Tui.Interactive;

internal static class TuiProfilingCounterNames
{
    public const string RenderCycle = "render-cycle";
    public const string LayoutApply = "layout-apply";
    public const string TranscriptItemRender = "transcript-item-render";
    public const string ChatRowGroupPlan = "chat.row-group.plan";
    public const string CompletionInvocation = "completion-invocation";
    public const string FileReferenceCompletion = "file-reference-completion";
    public const string FileReferenceFileSystem = "file-reference-filesystem";
    public const string FileReferenceGitVisibility = "file-reference-git-visibility";
}

internal sealed class TuiProfilingCounters
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public void Increment(string name)
        => _counts.AddOrUpdate(name, 1, static (_, current) => current + 1);

    public long GetCount(string name)
        => _counts.TryGetValue(name, out var count) ? count : 0;

    public IReadOnlyDictionary<string, long> Snapshot()
        => new Dictionary<string, long>(_counts, StringComparer.Ordinal);
}
