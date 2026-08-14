namespace PiSharp.Agent.Core.Tools;

/// <summary>
/// A pluggable content extractor for a file format that <c>read</c> cannot
/// decode as plain text (e.g. PDF). Registered extractors are consulted by the
/// built-in <c>read</c> tool between image detection and the UTF-8 text
/// fallback, so extracted documents flow through the same
/// offset/limit/truncation affordances as text files.
/// </summary>
public interface IFileContentExtractor
{
    /// <summary>Stable extractor id, e.g. "pdf".</summary>
    string Id { get; }

    /// <summary>
    /// Whether this extractor claims the given path/bytes (by extension and/or
    /// magic bytes). Return <c>true</c> only when extraction is likely to
    /// succeed; <see cref="ExtractAsync"/> may still return null if it yields no text.
    /// </summary>
    bool CanHandle(string path, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Extracts text from the given bytes. Return <c>null</c> when the content
    /// is not this extractor's (e.g. zero extractable text) so the read tool
    /// falls through to the next candidate or the UTF-8 fallback.
    /// </summary>
    Task<FileContentExtractionResult?> ExtractAsync(
        string path,
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a successful content extraction. The read tool applies its
/// normal offset/limit/truncation processing to <see cref="Text"/>.
/// </summary>
public sealed record FileContentExtractionResult(
    string Text,
    string? Note = null);
