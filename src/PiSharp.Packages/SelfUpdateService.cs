namespace PiSharp.Packages;
/// <summary>Orchestrates the self-update flow and the post-update daemon notice.</summary>
public sealed class SelfUpdateService
{
    private readonly ISelfUpdateMethod _method;

    public SelfUpdateService(ISelfUpdateMethod method)
    {
        _method = method;
    }

    public ISelfUpdateMethod Method => _method;

    public Task<SelfUpdateResult> UpdateAsync(string? addSource, bool offline, CancellationToken cancellationToken)
        => _method.UpdateAsync(addSource, offline, cancellationToken);

    /// <summary>Prints the post-update daemon notice when a lease file exists at the canonical path.
    /// No-op when the lease is absent (e.g. pre-P01).</summary>
    public async Task PrintDaemonNoticeAsync(string? leasePath, TextWriter writer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leasePath) || !File.Exists(leasePath)) return;
        await writer.WriteLineAsync(
            "A PiSharp daemon is still running from the previous version. Its sessions keep working; " +
            "stop it with `pisharp daemon stop` when convenient. The next interactive start will validate the " +
            "daemon lease and start its own daemon on a fresh port if the protocol versions differ.");
    }
}
