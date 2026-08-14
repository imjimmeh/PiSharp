using Xunit;
using System.Net;
using System.Text;
using PiSharp.Extensions;
using PiSharp.Research.Http;
using PiSharp.Research.Pdf;
using PiSharp.Extensions;
using PiSharp.Research.Web;

namespace PiSharp.Research.Tests;

public sealed class WebUrlResolverTests
{
    private const string ArxivAbsHtml = """
        <!DOCTYPE html>
        <html>
        <head>
          <meta name="citation_title" content="A Transformer Paper" />
          <meta name="citation_author" content="Jane Doe" />
          <meta name="citation_date" content="2024/01/01" />
          <meta name="citation_abstract" content="We propose a new architecture." />
          <meta name="citation_pdf_url" content="https://arxiv.org/pdf/2401.00001" />
        </head>
        <body><h1>A Transformer Paper</h1><p>Body text.</p></body>
        </html>
        """;

    private static WebUrlResolver Resolver(
        HttpMessageHandler handler,
        int maxBytes = 1024 * 1024,
        int pdfMaxBytes = 1024 * 1024,
        int pdfMaxPages = 100)
    {
        var http = new ResearchHttpClient(TimeSpan.FromSeconds(30), handler);
        return new WebUrlResolver("https", http, new PdfTextExtractor(pdfMaxBytes, pdfMaxPages), maxBytes);
    }

    private static InternalUrlRequest Request(string target, string? query = null)
        => new("https", target, query);

    [Fact]
    public async Task ArxivAbsPageReturnsStructuredAbstract()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ArxivAbsHtml, "text/html; charset=utf-8");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("arxiv.org/abs/2401.00001"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.NotNull(result.Content);
        Assert.Contains("Title: A Transformer Paper", result.Content);
        Assert.Contains("Authors: Jane Doe", result.Content);
        Assert.Contains("Date: 2024/01/01", result.Content);
        Assert.Contains("Abstract:", result.Content);
        Assert.Contains("We propose a new architecture.", result.Content);
        Assert.Contains("Full text: https://arxiv.org/pdf/2401.00001", result.Content);
    }

    [Fact]
    public async Task ArxivAbsPageWithoutMetadataStillResolves()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "<html><head></head><body>nothing</body></html>", "text/html");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("arxiv.org/abs/2401.00002"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains("Abstract: (none found on page)", result.Content);
    }

    [Fact]
    public async Task PdfUrlReturnsExtractedText()
    {
        var pdf = TestPdfBuilder.TextPdf("Web fetched PDF text");
        var handler = StubHttpMessageHandler.ForBytes(pdf, "application/pdf");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("arxiv.org/pdf/2401.00001"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains("Web fetched PDF text", result.Content);
    }

    [Fact]
    public async Task PdfDetectedByPathExtensionWithoutContentType()
    {
        var pdf = TestPdfBuilder.TextPdf("Extension detected");
        var handler = StubHttpMessageHandler.ForBytes(pdf, "application/octet-stream");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("example.com/paper.pdf"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains("Extension detected", result.Content);
    }

    [Fact]
    public async Task PlainHtmlIsStrippedToText()
    {
        var html = """
            <!DOCTYPE html>
            <html><head><title>Docs Page</title></head>
            <body>
              <script>var x = "ignore me";</script>
              <style>.cls { color: red; }</style>
              <h1>Heading</h1>
              <p>Some <b>important</b> content.</p>
            </body></html>
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, html, "text/html; charset=utf-8");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("docs.example.com/guide"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Contains("Title: Docs Page", result.Content);
        Assert.Contains("Heading", result.Content);
        Assert.Contains("Some important content.", result.Content);
        Assert.DoesNotContain("ignore me", result.Content);
        Assert.DoesNotContain("color: red", result.Content);
        Assert.DoesNotContain("<p>", result.Content);
    }

    [Fact]
    public async Task NonHtmlBodyPassesThroughAsText()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "plain text body", "text/plain");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("example.com/notes.txt"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("plain text body", result.Content);
    }

    [Fact]
    public async Task OverMaxBytesReturnsForbidden()
    {
        var handler = StubHttpMessageHandler.ForBytes(new byte[4096], "text/html", contentLength: 4096);
        var resolver = Resolver(handler, maxBytes: 1024);

        var result = await resolver.ResolveAsync(Request("big.example.com/page"), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.Forbidden, result.Error!.Kind);
        Assert.Contains("fetch.maxBytes", result.Error.Reason);
    }

    [Fact]
    public async Task StreamedBodyOverMaxBytesReturnsForbidden()
    {
        var handler = StubHttpMessageHandler.ForBytes(new byte[4096], "text/html"); // no declared length
        var resolver = Resolver(handler, maxBytes: 1024);

        var result = await resolver.ResolveAsync(Request("big.example.com/page"), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.Forbidden, result.Error!.Kind);
    }

    [Fact]
    public async Task ConnectionFailureReturnsResolutionFailed()
    {
        var handler = StubHttpMessageHandler.Failing(new HttpRequestException("connection refused"));
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("down.example.com/page"), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.ResolutionFailed, result.Error!.Kind);
        Assert.Contains("connection refused", result.Error.Reason);
    }

    [Fact]
    public async Task HttpErrorStatusReturnsResolutionFailed()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, "nope", "text/plain");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("example.com/missing"), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.ResolutionFailed, result.Error!.Kind);
        Assert.Contains("404", result.Error.Reason);
    }

    [Fact]
    public async Task InvalidTargetReturnsNotFound()
    {
        var resolver = Resolver(new StubHttpMessageHandler(HttpStatusCode.OK, "x", "text/plain"));

        var result = await resolver.ResolveAsync(Request(" "), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task QueryStringIsPreservedInUrl()
    {
        string? fetchedUrl = null;
        var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            fetchedUrl = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "text/plain"),
            };
        });
        var resolver = Resolver(handler);

        await resolver.ResolveAsync(new InternalUrlRequest("https", "example.com/search", "q=hello&page=2"), CancellationToken.None);

        Assert.Equal("https://example.com/search?q=hello&page=2", fetchedUrl);
    }

    [Fact]
    public async Task ArxivPdfWithoutExtractableTextReturnsNotFound()
    {
        var handler = StubHttpMessageHandler.ForBytes(TestPdfBuilder.ScannedPdf(), "application/pdf");
        var resolver = Resolver(handler);

        var result = await resolver.ResolveAsync(Request("arxiv.org/pdf/2401.00003"), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
        Assert.Contains("no extractable text", result.Error.Reason);
    }
}
