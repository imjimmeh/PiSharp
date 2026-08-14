using PiSharp.Extensions;
using PiSharp.InternalUrls.Services;

namespace PiSharp.InternalUrls.Resolvers;

/// <summary>
/// Resolves <c>diff://</c> to the most recent recorded edit diff and
/// <c>diff://&lt;path&gt;</c> to that file's most recent diff, from the
/// in-memory <see cref="DiffLedger"/>. No filesystem read occurs.
/// </summary>
public sealed class DiffUrlResolver(DiffLedger ledger, Func<string, string?>? pathNormalizer = null) : IInternalUrlResolver
{
    private readonly DiffLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly Func<string, string?>? _pathNormalizer = pathNormalizer;

    public string Scheme => "diff";

    public ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Target))
        {
            if (_ledger.TryGetLatest(out var path, out var diff))
                return ValueTask.FromResult(Resolved(path!, diff!));
            return ValueTask.FromResult(NotFound("No edit diffs recorded yet."));
        }

        // Guard the path target defensively: rejects absolute forms, "..", escapes.
        if (!InternalUrlSecurity.TryParseTarget(request.Target, out _))
            return ValueTask.FromResult(Blocked(request.Target));

        var normalized = _pathNormalizer?.Invoke(request.Target) ?? request.Target;
        if (normalized is not null && _ledger.TryGetForPath(normalized, out var specific))
            return ValueTask.FromResult(Resolved(normalized, specific));

        if (_ledger.TryGetForPath(request.Target, out var raw))
            return ValueTask.FromResult(Resolved(request.Target, raw));

        return ValueTask.FromResult(NotFound($"No recorded diff for '{request.Target}'."));
    }

    private static InternalUrlResult Resolved(string path, string content)
        => new(true, $"# diff for {path}\n\n{content}");

    private static InternalUrlResult Blocked(string target)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.TraversalBlocked, $"Traversal blocked in diff:// target '{target}'."));

    private static InternalUrlResult NotFound(string reason)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.NotFound, reason));
}
