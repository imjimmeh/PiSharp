namespace PiSharp.Extensions;

public sealed record InternalUrlRequest(
    string Scheme,          // lowercased scheme token before "://", e.g. "skill"
    string Target,          // the raw remainder after "scheme://" exactly as typed
    string? Query,          // substring after '?' in Target, if any (uninterpreted by core)
    int? Offset = null,     // line offset from the read tool input (1-indexed)
    int? Limit = null);     // line limit from the read tool input

public enum InternalUrlErrorKind { UnknownScheme, NotFound, TraversalBlocked, Forbidden, ResolutionFailed }

public sealed record InternalUrlError(InternalUrlErrorKind Kind, string Reason);

public sealed record InternalUrlResult(
    bool Resolved,                       // true when Content carries a document; false when Error is set
    string? Content,                     // text body of the resolved document (UTF-8)
    InternalUrlError? Error = null,      // set when !Resolved
    string? DetailPath = null);          // optional emitted "full output" path for very large content

/// <summary>
/// Provider contract for a single internal URL scheme (e.g. <c>skill://</c>).
/// Registered in the runtime-wide <see cref="InternalUrlRegistry"/> and
/// consulted by the read tool before filesystem resolution.
/// </summary>
public interface IInternalUrlResolver
{
    string Scheme { get; }               // scheme token this resolver owns, e.g. "skill"
    ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct);
}
