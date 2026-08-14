using System.Net;
using System.Text;
using PiSharp.Packages;
using Xunit;

namespace PiSharp.Cli.Tests.Packages;

public sealed class NuGetRegistryClientTests
{
    [Fact]
    public async Task GetLatestStableVersionAsync_ReturnsHighestStable()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"versions":["1.0.0","1.1.0-beta","1.1.0","2.0.0-preview","2.1.0"]}""",
                    Encoding.UTF8, "application/json")
            });
        var client = new NuGetRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestStableVersionAsync("PiSharp.Cli");

        Assert.Equal("2.1.0", result);
    }

    [Fact]
    public async Task GetLatestStableVersionAsync_ReturnsNull_WhenOnlyPrereleases()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"versions":["1.0.0-beta","1.0.0-rc.1"]}""", Encoding.UTF8, "application/json")
            });
        var client = new NuGetRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestStableVersionAsync("PiSharp.Cli");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestStableVersionAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new NuGetRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestStableVersionAsync("missing.package");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestStableVersionAsync_ReturnsNull_WhenInvalidJson()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", Encoding.UTF8, "text/plain")
            });
        var client = new NuGetRegistryClient(new HttpClient(handler));

        var result = await client.GetLatestStableVersionAsync("bad.package");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestStableVersionAsync_LowercasesIdInUrl()
    {
        Uri? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"versions":["1.0.0"]}""", Encoding.UTF8, "application/json")
            };
        });
        var client = new NuGetRegistryClient(new HttpClient(handler));

        await client.GetLatestStableVersionAsync("PiSharp.Cli");

        Assert.EndsWith("/pisharp.cli/index.json", captured?.ToString());
    }
}
