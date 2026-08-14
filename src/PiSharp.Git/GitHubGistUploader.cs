using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PiSharp.Git;

public sealed record GistUploadRequest(
    string FileName,
    string Content,
    bool IsPublic,
    string? Description,
    string Token);

/// <summary>Result of a gist upload. <see cref="Error"/> never contains the token.</summary>
public sealed record GistUploadResult(
    bool Success,
    string? Error,
    string? HtmlUrl,
    string? GistId,
    long? Bytes);

/// <summary>Uploads a file to GitHub Gists via the REST API. Internal seam — a future
/// GitLab-snippet or local-copy backend would implement this behind the same interface.</summary>
public interface IGistUploader
{
    Task<GistUploadResult> UploadAsync(GistUploadRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// GitHub Gists REST client. A single static <see cref="HttpClient"/> carries the
/// required <c>User-Agent</c> and <c>X-GitHub-Api-Version</c> headers. The size guard
/// (per-file gist limit) rejects oversize uploads before any network I/O.
/// </summary>
public sealed class GitHubGistUploader(GitPluginOptions options, HttpMessageHandler? handler = null) : IGistUploader
{
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PiSharp");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GistUploadResult> UploadAsync(GistUploadRequest request, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetByteCount(request.Content);
        if (bytes > options.ShareMaxBytes)
        {
            return new GistUploadResult(false,
                $"The file is {bytes} bytes, exceeding the {options.ShareMaxBytes}-byte gist per-file limit. " +
                "Shorten the file or raise share.maxBytes.", null, null, bytes);
        }

        var body = JsonSerializer.Serialize(new
        {
            description = request.Description ?? $"PiSharp session upload",
            @public = request.IsPublic,
            files = new Dictionary<string, object>
            {
                [request.FileName] = new { content = request.Content }
            }
        }, JsonOptions);

        var http = handler is null ? SharedClient.Value : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.GithubApiBaseUrl.TrimEnd('/')}/gists");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Token);
            message.Headers.UserAgent.ParseAdd("PiSharp");
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(message, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new GistUploadResult(false, FormatError((int)response.StatusCode, responseBody), null, null, bytes);
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var htmlUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
            var gistId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(htmlUrl) || string.IsNullOrWhiteSpace(gistId))
            {
                return new GistUploadResult(false, "The gist API response did not include an id or url.", null, null, bytes);
            }

            return new GistUploadResult(true, null, htmlUrl, gistId, bytes);
        }
        catch (HttpRequestException ex)
        {
            return new GistUploadResult(false, $"Gist upload failed: {ex.Message}", null, null, bytes);
        }
        finally
        {
            if (handler is not null)
            {
                http.Dispose();
            }
        }
    }

    private static string FormatError(int statusCode, string responseBody)
    {
        var excerpt = ExtractMessage(responseBody);
        return $"GitHub gist upload failed (HTTP {statusCode}): {excerpt}";
    }

    private static string ExtractMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return Truncate(text, 500);
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw-excerpt path.
        }

        var raw = responseBody.Trim();
        if (raw.Length == 0)
        {
            return "empty response body.";
        }

        return Truncate(raw, 500);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
