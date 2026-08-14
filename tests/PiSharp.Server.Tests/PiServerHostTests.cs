using PiSharp.Server.Hosting;
using Xunit;

namespace PiSharp.Server.Tests;

public sealed class PiServerHostTests
{
    [Fact]
    public async Task StartAsync_ListensAndServesHealth()
    {
        var host = new PiServerHost(new PiServerHostOptions { ApiKey = "test-key" });
        await host.StartAsync(port: 0);
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"http://127.0.0.1:{host.Port}/health");
            Assert.True(response.IsSuccessStatusCode);
        }
        finally { await host.StopAsync(); }
    }
}
