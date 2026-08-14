using System.Net;
using System.Text;
using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public class NpmRegistryClientTests
{
    [Fact]
    public async Task GetLatestVersionAsync_ReturnsVersion_WhenRegistryResponds()
    {
        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"2.3.4"}""", Encoding.UTF8, "application/json")
            });
        var client = new NpmRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestVersionAsync("my-package");

        Assert.Equal("2.3.4", result);
    }

    [Fact]
    public async Task GetLatestVersionAsync_ReturnsNull_WhenRegistryReturnsError()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new NpmRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestVersionAsync("missing-package");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestVersionAsync_ReturnsNull_WhenResponseIsInvalidJson()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", Encoding.UTF8, "text/plain")
            });
        var client = new NpmRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestVersionAsync("bad-package");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestVersionAsync_CallsCorrectUrl()
    {
        Uri? capturedUri = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"1.0.0"}""", Encoding.UTF8, "application/json")
            };
        });
        var client = new NpmRegistryClient(new HttpClient(handler));

        await client.GetLatestVersionAsync("@scope/my-pkg");

        Assert.Equal("https://registry.npmjs.org/%40scope%2Fmy-pkg/latest", capturedUri?.ToString());
    }
}

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(handler(request));
}
