using System.Text;

namespace PiSharp.Research.Tests;

/// <summary>
/// Builds minimal, structurally valid PDFs in memory (correct xref offsets) for
/// extractor tests: text pages, image-only ("scanned") pages, and an
/// encrypted-trailer document. No binary fixtures committed to the repo.
/// </summary>
internal static class TestPdfBuilder
{
    /// <summary>Builds a one-page PDF whose content stream draws the given text.</summary>
    public static byte[] TextPdf(string text) => Build([text]);

    /// <summary>Builds a multi-page PDF, one page per entry.</summary>
    public static byte[] TextPdf(params string[] pageTexts) => Build(pageTexts);

    /// <summary>Builds a one-page PDF with an image and no text (scanned-style).</summary>
    public static byte[] ScannedPdf() => Build([""], includeImage: true);

    /// <summary>Builds a PDF whose trailer declares standard encryption (PdfPig refuses it).</summary>
    public static byte[] EncryptedPdf() => Build(["secret"], encrypted: true);

    private static byte[] Build(string[] pageTexts, bool includeImage = false, bool encrypted = false)
    {
        var body = new StringBuilder();
        body.Append("%PDF-1.4\n");
        var offsets = new List<long>(); // offsets[i] = byte offset of object i+1

        void AddObject(string content)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(body.ToString()));
            body.Append(content);
        }

        void AddStream(int objectNumber, string content)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(body.ToString()));
            var contentBytes = Encoding.ASCII.GetByteCount(content);
            body.Append($"{objectNumber} 0 obj\n<< /Length {contentBytes} >>\nstream\n{content}endstream\nendobj\n");
        }

        AddObject("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var pageCount = pageTexts.Length;
        var firstContentRef = 3 + pageCount;
        var imageRef = firstContentRef + pageCount;
        var fontRef = imageRef + (includeImage ? 1 : 0);

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"));
        AddObject($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var resources = includeImage
                ? $"<< /Font << /F1 {fontRef} 0 R >> /XObject << /Im1 {imageRef} 0 R >> >>"
                : $"<< /Font << /F1 {fontRef} 0 R >> >>";
            AddObject($"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {firstContentRef + i} 0 R /Resources {resources} >>\nendobj\n");
        }

        for (var i = 0; i < pageCount; i++)
        {
            var content = includeImage
                ? "q 612 0 0 792 0 0 cm /Im1 Do Q\n"
                : $"BT /F1 12 Tf 72 720 Td ({Escape(pageTexts[i])}) Tj ET\n";
            AddStream(firstContentRef + i, content);
        }

        if (includeImage)
        {
            AddObject($"{imageRef} 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\nA\nendstream\nendobj\n");
        }

        AddObject($"{fontRef} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var objectCount = fontRef + 1; // objects are 1..fontRef inclusive
        var xrefOffset = Encoding.ASCII.GetByteCount(body.ToString());
        body.Append("xref\n");
        body.Append($"0 {objectCount}\n");
        body.Append("0000000000 65535 f \n");
        for (var i = 1; i < objectCount; i++)
        {
            body.Append($"{offsets[i - 1]:D10} 00000 n \n");
        }

        var trailer = encrypted
            ? $"<< /Size {objectCount} /Root 1 0 R /Encrypt << /Filter /Standard /V 2 /R 3 /Length 40 /O <{new string('0', 32)}> /U <{new string('0', 32)}> /P -44 >> >>"
            : $"<< /Size {objectCount} /Root 1 0 R >>";
        body.Append($"trailer\n{trailer}\n");
        body.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(body.ToString());
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", " ");
}
