using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PiSharp.Git.Tests;

public sealed class GitHubGistUploaderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    private static GitHubGistUploader UploaderWith(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new GitPluginOptions { GithubApiBaseUrl = "https://api.github.com" }, new StubHandler(respond));

    private static GistUploadRequest Request(string content = "hello", string token = "ghp_ok")
        => new("note.txt", content, IsPublic: false, "desc", token);

    [Fact]
    public async Task SuccessfulUploadReturnsUrlAndId()
    {
        var uploader = UploaderWith(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.github.com/gists", request.RequestUri!.ToString());
            Assert.Equal("Bearer ghp_ok", request.Headers.Authorization!.ToString());
            Assert.Contains("PiSharp", request.Headers.UserAgent.ToString());

            var body = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.False(body.GetProperty("public").GetBoolean());
            Assert.Equal("hello", body.GetProperty("files").GetProperty("note.txt").GetProperty("content").GetString());

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"html_url":"https://gist.github.com/user/abc123","id":"abc123"}""",
                    Encoding.UTF8, "application/json")
            };
        });

        var result = await uploader.UploadAsync(Request());

        Assert.True(result.Success);
        Assert.Equal("https://gist.github.com/user/abc123", result.HtmlUrl);
        Assert.Equal("abc123", result.GistId);
        Assert.Equal(5L, result.Bytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task PublicVisibilityIsSent()
    {
        var uploader = UploaderWith(request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.True(body.GetProperty("public").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"html_url":"u","id":"i"}""", Encoding.UTF8, "application/json")
            };
        });

        var result = await uploader.UploadAsync(Request() with { IsPublic = true });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task OversizeContentRejectedBeforeNetwork()
    {
        var uploader = UploaderWith(_ => throw new InvalidOperationException("network must not be hit"));
        var result = await uploader.UploadAsync(Request(new string('x', 1_000_001)));
        Assert.False(result.Success);
        Assert.Contains("exceeding", result.Error);
    }

    [Fact]
    public async Task ApiErrorExposesMessageNotToken()
    {
        var uploader = UploaderWith(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"Bad credentials"}""", Encoding.UTF8, "application/json")
        });

        var result = await uploader.UploadAsync(Request(token: "ghp_secret"));

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.DoesNotContain("ghp_secret", result.Error);
    }

    [Fact]
    public async Task NetworkFailureReturnsError()
    {
        var uploader = UploaderWith(_ => throw new HttpRequestException("boom"));
        var result = await uploader.UploadAsync(Request());
        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
    }
}
