namespace PiSharp.Tui.Interactive;

public sealed record TuiExtensionLoadFailure(string Path, string? Diagnostic = null);

public sealed record TuiExtensionLoadStatus(
    int Total,
    int Active,
    int BlockingActive,
    int Ready,
    int Failed,
    IReadOnlyList<TuiExtensionLoadFailure>? Failures = null)
{
    public bool IsLoading => Active > 0;
    public bool BlocksInput => BlockingActive > 0;
    public IReadOnlyList<TuiExtensionLoadFailure> FailureDetails => Failures ?? [];

    public string FormatCompletedMessage()
    {
        if (Failed <= 0) return $"Extensions loaded: {Ready}/{Total} ready.";
        var message = $"Extensions loaded: {Ready}/{Total} ready, {Failed} failed.";
        if (FailureDetails.Count == 0) return message;

        var details = FailureDetails.Select(failure => string.IsNullOrWhiteSpace(failure.Diagnostic)
            ? $"- {failure.Path}"
            : $"- {failure.Path}: {failure.Diagnostic}");
        return message + Environment.NewLine + "Failed extensions:" + Environment.NewLine + string.Join(Environment.NewLine, details);
    }
}
