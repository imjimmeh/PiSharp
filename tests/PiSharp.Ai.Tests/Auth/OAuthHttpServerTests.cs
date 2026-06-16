using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class OAuthHttpServerTests
{
    [Fact]
    public async Task StartsAndListensOnPort()
    {
        var server = new OAuthHttpServer(0);
        try
        {
            await server.StartAsync();
            Assert.True(server.IsListening);
            Assert.True(server.Port > 0);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ReceivesCallbackWithCode()
    {
        var server = new OAuthHttpServer(0);
        try
        {
            await server.StartAsync();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync($"{server.RedirectUri}?code=test-code&state=test-state");
            Assert.True(response.IsSuccessStatusCode);
            var result = await server.WaitForCodeAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(result);
            Assert.Equal("test-code", result!.Value.Code);
            Assert.Equal("test-state", result.Value.State);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task SupportsCustomCallbackPath()
    {
        var server = new OAuthHttpServer(0, "127.0.0.1", "/auth/callback");
        try
        {
            await server.StartAsync();
            Assert.EndsWith("/auth/callback", server.RedirectUri, StringComparison.Ordinal);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync($"{server.RedirectUri}?code=test-code&state=test-state");

            Assert.True(response.IsSuccessStatusCode);
            var result = await server.WaitForCodeAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(result);
            Assert.Equal("test-code", result!.Value.Code);
            Assert.Equal("test-state", result.Value.State);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ReturnsErrorPageForMissingCode()
    {
        var server = new OAuthHttpServer(0);
        try
        {
            await server.StartAsync();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync($"{server.RedirectUri}");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task WaitForCodeTimesOutWhenNoRequest()
    {
        var server = new OAuthHttpServer(0);
        try
        {
            await server.StartAsync();
            var result = await server.WaitForCodeAsync(TimeSpan.FromMilliseconds(500));
            Assert.Null(result);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task CancelWaitReturnsNull()
    {
        var server = new OAuthHttpServer(0);
        try
        {
            await server.StartAsync();
            server.CancelWait();
            var result = await server.WaitForCodeAsync(TimeSpan.FromSeconds(5));
            Assert.Null(result);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void DisposesCleanly()
    {
        var server = new OAuthHttpServer(0);
        server.Dispose();
        Assert.False(server.IsListening);
    }
}
