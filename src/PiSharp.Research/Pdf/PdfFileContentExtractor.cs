using PiSharp.Agent.Core.Tools;

namespace PiSharp.Research.Pdf;

/// <summary>
/// <c>IFileContentExtractor</c> facade for PDFs: claims <c>.pdf</c> paths or
/// <c>%PDF-</c> magic bytes, extracts text via <see cref="PdfTextExtractor"/>,
/// and refuses documents over the byte cap with a model-visible note. The read
/// tool applies its normal offset/limit/truncation processing to the result.
/// </summary>
public sealed class PdfFileContentExtractor : IFileContentExtractor
{
    private readonly PdfTextExtractor _extractor;
    private readonly int _maxBytes;

    public PdfFileContentExtractor(int maxBytes, int maxPages)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPages));
        _maxBytes = maxBytes;
        _extractor = new PdfTextExtractor(maxBytes, maxPages);
    }

    public string Id => "pdf";

    public bool CanHandle(string path, ReadOnlySpan<byte> bytes)
        => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
           || LooksLikePdf(bytes);

    public Task<FileContentExtractionResult?> ExtractAsync(
        string path,
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.Length > _maxBytes)
        {
            var note = $"[PDF not extracted: {bytes.Length:N0} bytes exceeds the configured maximum of {_maxBytes:N0} bytes (extensions.pisharp-research.pdf.maxBytes).]";
            return Task.FromResult<FileContentExtractionResult?>(new FileContentExtractionResult(string.Empty, note));
        }

        var text = _extractor.TryExtract(bytes, cancellationToken);
        if (text is null)
        {
            return Task.FromResult<FileContentExtractionResult?>(null);
        }

        var sizeNote = $"[Extracted text from PDF, {bytes.Length:N0} bytes]";
        return Task.FromResult<FileContentExtractionResult?>(new FileContentExtractionResult(text, sizeNote));
    }

    private static bool LooksLikePdf(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 5
           && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-';
}
