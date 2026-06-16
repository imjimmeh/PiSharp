namespace PiSharp.Runtime;

public sealed record ExtensionLoadFailure(string Path, string? Diagnostic);

public sealed record ExtensionLoadSummary(
    int Total,
    int Active,
    int BlockingActive,
    int Ready,
    int Failed,
    IReadOnlyList<ExtensionLoadFailure> FailedDiagnostics)
{
    public bool IsLoading => Active > 0;
    public bool BlocksInput => BlockingActive > 0;

    public static ExtensionLoadSummary From(IEnumerable<ExtensionLoadStatus> statuses)
    {
        var list = statuses.ToArray();
        var active = list.Count(status => status.State is ExtensionLoadState.Discovered or ExtensionLoadState.Pending or ExtensionLoadState.Loading or ExtensionLoadState.BackgroundLoading);
        var blockingActive = list.Count(status => status.State is ExtensionLoadState.Discovered or ExtensionLoadState.Pending or ExtensionLoadState.Loading);
        var ready = list.Count(status => status.State == ExtensionLoadState.Ready);
        var failed = list.Count(status => status.State == ExtensionLoadState.Failed);
        var failedDiagnostics = list
            .Where(status => status.State == ExtensionLoadState.Failed)
            .OrderBy(status => status.ExtensionPath, StringComparer.Ordinal)
            .Select(status => new ExtensionLoadFailure(status.ExtensionPath, status.Diagnostic))
            .ToArray();

        return new ExtensionLoadSummary(list.Length, active, blockingActive, ready, failed, failedDiagnostics);
    }
}
