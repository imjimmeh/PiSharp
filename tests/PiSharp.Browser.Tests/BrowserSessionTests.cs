using PiSharp.Browser.Runtime;
using PiSharp.Browser.Tests.Fakes;
using Xunit;

namespace PiSharp.Browser.Tests;

public class BrowserSessionTests
{
    private static readonly BrowserToolOptions Options = new();

    [Fact]
    public void NotOpen_WhenDriverNeverCreated()
    {
        var session = new BrowserSession(new FakeBrowserDriverFactory(), Options);
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task Open_CreatesDriverLazilyAndOpens()
    {
        var factory = new FakeBrowserDriverFactory();
        var session = new BrowserSession(factory, Options);

        var (url, title) = await session.OpenAsync("http://localhost/a", null, 30000, CancellationToken.None);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal("http://localhost/a", url);
        Assert.Equal("Open Title", title);
        Assert.True(session.IsOpen);
        Assert.Equal("http://localhost/a", session.CurrentUrl);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Open_ReusesDriverAcrossCalls()
    {
        var factory = new FakeBrowserDriverFactory();
        var session = new BrowserSession(factory, Options);

        await session.OpenAsync("http://localhost/1", null, 30000, CancellationToken.None);
        await session.OpenAsync("http://localhost/2", null, 30000, CancellationToken.None);

        Assert.Equal(1, factory.CreateCount); // driver should be created once and reused for the session
        Assert.Equal("http://localhost/2", session.CurrentUrl);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Run_BeforeOpen_Throws()
    {
        var factory = new FakeBrowserDriverFactory();
        factory.Driver.IsOpen = false;
        var session = new BrowserSession(factory, Options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync("1", true, CancellationToken.None));

        Assert.Contains("not open", ex.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ClosesCreatedDriver()
    {
        var factory = new FakeBrowserDriverFactory();
        var session = new BrowserSession(factory, Options);

        await session.OpenAsync("http://localhost/a", null, 30000, CancellationToken.None);
        await session.DisposeAsync();

        Assert.True(factory.Driver.DisposeCalled);
    }

    [Fact]
    public async Task Dispose_WithoutOpen_IsSafeAndDisposesNothing()
    {
        var factory = new FakeBrowserDriverFactory();
        var session = new BrowserSession(factory, Options);

        await session.DisposeAsync();

        Assert.Equal(0, factory.CreateCount);
        Assert.False(factory.Driver.DisposeCalled);
    }

    [Fact]
    public async Task Dispose_Twice_IsSafe()
    {
        var factory = new FakeBrowserDriverFactory();
        var session = new BrowserSession(factory, Options);
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.True(true);
    }
}
