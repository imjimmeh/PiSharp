using Xunit;
using PiSharp.Research.Pdf;

namespace PiSharp.Research.Tests;

public sealed class PdfTextExtractorTests
{
    private static readonly PdfTextExtractor DefaultExtractor = new(maxBytes: 1024 * 1024, maxPages: 100);

    [Fact]
    public void ExtractsTextFromGeneratedPdf()
    {
        var bytes = TestPdfBuilder.TextPdf("Hello from PdfPig");

        var text = DefaultExtractor.TryExtract(bytes, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("Hello from PdfPig", text);
    }

    [Fact]
    public void HandlesMultiWordUnicodeSafeText()
    {
        var bytes = TestPdfBuilder.TextPdf("The quick brown fox jumps over the lazy dog");

        var text = DefaultExtractor.TryExtract(bytes, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("quick brown fox", text);
    }

    [Fact]
    public void ExtractsAllPagesUpToPageCap()
    {
        var bytes = TestPdfBuilder.TextPdf("page one", "page two", "page three");
        var extractor = new PdfTextExtractor(maxBytes: 1024 * 1024, maxPages: 2);

        var text = extractor.TryExtract(bytes, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("page one", text);
        Assert.Contains("page two", text);
        Assert.DoesNotContain("page three", text);
    }

    [Fact]
    public void ReturnsNullForScannedImageOnlyPdf()
    {
        var bytes = TestPdfBuilder.ScannedPdf();

        var text = DefaultExtractor.TryExtract(bytes, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public void ReturnsNullForEncryptedPdf()
    {
        var bytes = TestPdfBuilder.EncryptedPdf();

        var text = DefaultExtractor.TryExtract(bytes, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public void ReturnsNullForGarbageBytes()
    {
        var bytes = "this is not a pdf at all"u8.ToArray();

        var text = DefaultExtractor.TryExtract(bytes, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public void ReturnsNullForPdfMagicGarbageThatFailsParsing()
    {
        var bytes = "%PDF-1.4\nthis is garbage, not a real pdf structure\n%%EOF"u8.ToArray();

        var text = DefaultExtractor.TryExtract(bytes, CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public void ReturnsNullForEmptyBytes()
    {
        var text = DefaultExtractor.TryExtract(Array.Empty<byte>(), CancellationToken.None);

        Assert.Null(text);
    }

    [Fact]
    public void ReturnsNullOverByteCap()
    {
        var bytes = TestPdfBuilder.TextPdf("tiny");
        var extractor = new PdfTextExtractor(maxBytes: bytes.Length - 1, maxPages: 100);

        var text = extractor.TryExtract(bytes, CancellationToken.None);

        Assert.Null(text);
    }
}

public sealed class PdfFileContentExtractorTests
{
    [Fact]
    public void ClaimsPdfExtensionAndMagic()
    {
        var extractor = new PdfFileContentExtractor(maxBytes: 1024 * 1024, maxPages: 100);
        var pdfBytes = TestPdfBuilder.TextPdf("x");

        Assert.True(extractor.CanHandle("paper.pdf", pdfBytes));
        Assert.True(extractor.CanHandle("noextension", pdfBytes));
        Assert.True(extractor.CanHandle("notes.txt", pdfBytes)); // magic bytes win over the path extension
        Assert.True(extractor.CanHandle("paper.pdf", "plain text"u8.ToArray())); // the .pdf extension claims; extraction falls through to null
    }

    [Fact]
    public async Task ExtractsTextThroughFacade()
    {
        var extractor = new PdfFileContentExtractor(maxBytes: 1024 * 1024, maxPages: 100);
        var bytes = TestPdfBuilder.TextPdf("Facade text");

        var result = await extractor.ExtractAsync("paper.pdf", bytes, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("Facade text", result.Text);
        Assert.NotNull(result.Note);
        Assert.Contains("PDF", result.Note);
    }

    [Fact]
    public async Task ReturnsNullForScannedPdf()
    {
        var extractor = new PdfFileContentExtractor(maxBytes: 1024 * 1024, maxPages: 100);

        var result = await extractor.ExtractAsync("scan.pdf", TestPdfBuilder.ScannedPdf(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RefusesOverByteCapWithNote()
    {
        var bytes = TestPdfBuilder.TextPdf("x");
        var extractor = new PdfFileContentExtractor(maxBytes: bytes.Length - 1, maxPages: 100);

        var result = await extractor.ExtractAsync("big.pdf", bytes, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Text);
        Assert.Contains("exceeds the configured maximum", result.Note);
    }

    [Fact]
    public async Task FallsThroughForNonPdfBytes()
    {
        var extractor = new PdfFileContentExtractor(maxBytes: 1024 * 1024, maxPages: 100);

        var result = await extractor.ExtractAsync("notes.txt", "plain text"u8.ToArray(), CancellationToken.None);

        Assert.Null(result);
    }
}
