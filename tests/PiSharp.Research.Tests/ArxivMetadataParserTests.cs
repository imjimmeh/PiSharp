using Xunit;
using PiSharp.Research.Web;

namespace PiSharp.Research.Tests;

public sealed class ArxivMetadataParserTests
{
    private const string ArxivHtml =
        """
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="citation_title" content="Attention Is All You Need" />
          <meta name="citation_author" content="Ashish Vaswani" />
          <meta name="citation_author" content="Noam Shazeer" />
          <meta name="citation_date" content="2017/06/12" />
          <meta name="citation_abstract" content="The dominant sequence transduction models are based on complex recurrent or convolutional neural networks." />
          <meta name="citation_pdf_url" content="https://arxiv.org/pdf/1706.03762" />
        </head>
        <body>... page content ...</body>
        </html>
        """;

    [Fact]
    public void ParsesCitationMetaTags()
    {
        var meta = ArxivMetadataParser.Parse(ArxivHtml);

        Assert.Equal("Attention Is All You Need", meta.Title);
        Assert.Equal(["Ashish Vaswani", "Noam Shazeer"], meta.Authors);
        Assert.Equal("2017/06/12", meta.Date);
        Assert.Contains("dominant sequence transduction models", meta.Abstract);
        Assert.Equal("https://arxiv.org/pdf/1706.03762", meta.PdfUrl);
    }

    [Fact]
    public void HandlesSingleQuotedAttributes()
    {
        var html = """
            <meta name='citation_title' content='Single Quotes Title'>
            <meta name='citation_pdf_url' content='https://arxiv.org/pdf/1.00001'>
            """;

        var meta = ArxivMetadataParser.Parse(html);

        Assert.Equal("Single Quotes Title", meta.Title);
        Assert.Equal("https://arxiv.org/pdf/1.00001", meta.PdfUrl);
    }

    [Fact]
    public void HandlesWhitespaceAroundEquals()
    {
        var html = """<meta name = "citation_title" content = "Padded Title">""";

        var meta = ArxivMetadataParser.Parse(html);

        Assert.Equal("Padded Title", meta.Title);
    }

    [Fact]
    public void DecodesHtmlEntitiesInContent()
    {
        var html = """<meta name="citation_abstract" content="R&amp;D costs &lt; expectations">""";

        var meta = ArxivMetadataParser.Parse(html);

        Assert.Equal("R&D costs < expectations", meta.Abstract);
    }

    [Fact]
    public void MissingTagsYieldNulls()
    {
        var meta = ArxivMetadataParser.Parse("<html><head><title>No meta here</title></head></html>");

        Assert.Null(meta.Title);
        Assert.Empty(meta.Authors);
        Assert.Null(meta.Date);
        Assert.Null(meta.Abstract);
        Assert.Null(meta.PdfUrl);
    }

    [Fact]
    public void IgnoresNonCitationMetaTags()
    {
        var html = """
            <meta name="description" content="some description">
            <meta name="citation_title" content="The Real Title">
            """;

        var meta = ArxivMetadataParser.Parse(html);

        Assert.Equal("The Real Title", meta.Title);
    }

    [Fact]
    public void MalformedHtmlNeverThrows()
    {
        var meta = ArxivMetadataParser.Parse("<meta name='citation_title' content='unterminated");

        Assert.Null(meta.Title);
    }

    [Fact]
    public void CaseInsensitiveTagAndAttributeNames()
    {
        var html = """<META NAME="CITATION_TITLE" CONTENT="Uppercase Meta">""";

        var meta = ArxivMetadataParser.Parse(html);

        Assert.Equal("Uppercase Meta", meta.Title);
    }
}
