using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PiSharp.Research.Pdf;

/// <summary>
/// Extracts text from PDF bytes using PdfPig. This is the ONLY type in the
/// repository that references PdfPig — the library is pinned to 0.1.15 and any
/// future upgrade touches this file alone. Non-PDF bytes, encrypted documents,
/// malformed files, and scanned (image-only) PDFs all yield null so callers can
/// fall back to the UTF-8 text path.
/// </summary>
public sealed class PdfTextExtractor
{
    private readonly int _maxBytes;
    private readonly int _maxPages;

    public PdfTextExtractor(int maxBytes, int maxPages)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPages));
        _maxBytes = maxBytes;
        _maxPages = maxPages;
    }

    /// <summary>
    /// Returns the extracted text (reading order, double newlines between
    /// paragraphs) or null when the bytes are not an extractable PDF: wrong
    /// magic, over the byte cap, encrypted, malformed, or zero extractable text.
    /// </summary>
    public string? TryExtract(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > _maxBytes) return null;
        if (!LooksLikePdf(bytes)) return null;

        try
        {
            using var document = PdfDocument.Open(bytes.ToArray());
            var builder = new StringBuilder();
            var pages = 0;
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pages >= _maxPages) break;
                var text = ContentOrderTextExtractor.GetText(page, addDoubleNewline: true);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Append(text);
                    builder.Append('\n');
                }

                pages++;
            }

            var result = builder.ToString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Encrypted (no password), malformed xref, or any other parsing
            // failure: report "no extractable text" rather than crashing read.
            return null;
        }
    }

    private static bool LooksLikePdf(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 5
           && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-';
}
