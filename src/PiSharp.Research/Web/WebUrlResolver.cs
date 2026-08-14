using System.Net;
using System.Text;
using PiSharp.Extensions;
using PiSharp.Research.Http;
using PiSharp.Research.Pdf;

namespace PiSharp.Research.Web;

/// <summary>
/// Resolves <c>https://</c> and <c>http://</c> targets for the read tool (one
/// instance per scheme, registered via <c>IExtensionApi.Urls.RegisterResolver</c>):
/// arxiv abstract pages become structured citation text, PDF URLs become
/// extracted PDF text (shared <see cref="PdfTextExtractor"/>), and any other
/// HTML becomes plain text. Size caps and timeouts come from settings
/// (<c>extensions.pisharp-research.fetch.*</c>, <c>pdf.*</c>).
/// </summary>
public sealed class WebUrlResolver : IInternalUrlResolver
{
    private readonly ResearchHttpClient _http;
    private readonly PdfTextExtractor _pdfExtractor;
    private readonly int _maxBytes;
    private readonly string _scheme;

    public WebUrlResolver(
        string scheme,
        ResearchHttpClient http,
        PdfTextExtractor pdfExtractor,
        int maxBytes)
    {
        if (!string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("WebUrlResolver only supports the https and http schemes.", nameof(scheme));
        }

        _scheme = scheme.ToLowerInvariant();
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _pdfExtractor = pdfExtractor ?? throw new ArgumentNullException(nameof(pdfExtractor));
        _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }

    public string Scheme => _scheme;

    public async ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
    {
        var url = BuildUrl(request);
        if (url is null)
        {
            return Failure(InternalUrlErrorKind.NotFound, $"Invalid URL '{request.Target}'.");
        }

        try
        {
            if (IsArxivAbstract(request.Target))
            {
                var html = await FetchStringAsync(url, ct).ConfigureAwait(false);
                if (html is null)
                {
                    return Failure(InternalUrlErrorKind.NotFound, $"The arxiv abstract page at '{url}' could not be fetched.");
                }

                return new InternalUrlResult(true, FormatArxivAbstract(html), Error: null);
            }

            var (contentType, bytes, overLimit) = await FetchBytesAsync(url, ct).ConfigureAwait(false);
            if (overLimit)
            {
                return Failure(InternalUrlErrorKind.Forbidden, $"'{url}' exceeds the fetch.maxBytes cap of {_maxBytes:N0} bytes (extensions.pisharp-research.fetch.maxBytes).");
            }

            if (bytes is null)
            {
                return Failure(InternalUrlErrorKind.NotFound, $"The URL '{url}' could not be fetched.");
            }

            if (IsPdf(contentType, url))
            {
                var text = _pdfExtractor.TryExtract(bytes, ct);
                return text is null
                    ? Failure(InternalUrlErrorKind.NotFound, $"'{url}' is a PDF with no extractable text (scanned image PDF, encrypted, or over the pdf.maxBytes cap).")
                    : new InternalUrlResult(true, text, Error: null);
            }

            var encoding = DetectEncoding(contentType);
            var decoded = encoding.GetString(bytes);
            var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                         || (contentType.Length == 0 && LooksLikeHtml(decoded));
            var textContent = isHtml ? HtmlToText.Convert(decoded, _maxBytes) : decoded;
            if (textContent.Length > _maxBytes) textContent = textContent[.._maxBytes];
            return new InternalUrlResult(true, textContent, Error: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(InternalUrlErrorKind.ResolutionFailed, $"Fetching '{url}' timed out (extensions.pisharp-research.fetch.timeoutSeconds).");
        }
        catch (HttpRequestException exception)
        {
            return Failure(InternalUrlErrorKind.ResolutionFailed, $"Fetching '{url}' failed: {exception.Message}");
        }
    }

    /// <summary>Formats an arxiv abstract page into a structured text document.</summary>
    public static string FormatArxivAbstract(string html)
    {
        var meta = ArxivMetadataParser.Parse(html);
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(meta.Title))
        {
            builder.Append("Title: ").Append(meta.Title.Trim()).Append('\n');
        }

        if (meta.Authors.Count > 0)
        {
            builder.Append("Authors: ").Append(string.Join(", ", meta.Authors)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(meta.Date))
        {
            builder.Append("Date: ").Append(meta.Date.Trim()).Append('\n');
        }

        builder.Append('\n');
        if (!string.IsNullOrWhiteSpace(meta.Abstract))
        {
            builder.Append("Abstract:\n").Append(meta.Abstract.Trim()).Append('\n');
        }
        else
        {
            builder.Append("Abstract: (none found on page)\n");
        }

        if (!string.IsNullOrWhiteSpace(meta.PdfUrl))
        {
            builder.Append('\n').Append("Full text: ").Append(meta.PdfUrl.Trim()).Append('\n');
        }

        return builder.ToString();
    }

    private string? BuildUrl(InternalUrlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Target)) return null;
        var url = _scheme + "://" + request.Target + (request.Query is null ? string.Empty : "?" + request.Query);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "https" or "http" ? uri.ToString() : null;
    }

    private static bool IsArxivAbstract(string target)
    {
        var pathStart = target.IndexOf('/');
        if (pathStart < 0) return false;
        var host = target[..pathStart];
        var path = target[pathStart..];
        var queryStart = path.IndexOf('?');
        if (queryStart >= 0) path = path[..queryStart];
        return host.EndsWith("arxiv.org", StringComparison.OrdinalIgnoreCase)
               && path.StartsWith("/abs/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPdf(string contentType, string url)
    {
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return true;
        var pathStart = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathEnd = url.IndexOfAny(['?', '#']);
        var path = pathEnd < 0 ? url[pathStart..] : url[pathStart..pathEnd];
        return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding DetectEncoding(string contentType)
    {
        var charset = "charset=";
        var index = contentType.IndexOf(charset, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var value = contentType[(index + charset.Length)..].Trim().Trim('"', '\'');
            value = value.Split(';', 2)[0].Trim();
            try
            {
                return Encoding.GetEncoding(value);
            }
            catch (ArgumentException)
            {
                // Unknown charset name: fall back to UTF-8.
            }
        }

        return Encoding.UTF8;
    }

    private static bool LooksLikeHtml(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> FetchStringAsync(string url, CancellationToken ct)
    {
        var (contentType, bytes, _) = await FetchBytesAsync(url, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        return DetectEncoding(contentType).GetString(bytes);
    }

    private async Task<(string ContentType, byte[]? Bytes, bool OverLimit)> FetchBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        if (response.Content.Headers.ContentLength is { } declared && declared > _maxBytes)
        {
            return (contentType, null, true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > _maxBytes)
            {
                return (contentType, null, true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (contentType, buffer.ToArray(), false);
    }

    private static InternalUrlResult Failure(InternalUrlErrorKind kind, string reason)
        => new(false, null, new InternalUrlError(kind, reason));
}
