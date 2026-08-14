using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Browser.Runtime;
using PiSharp.Browser.Tests.Fakes;
using PiSharp.Extensions.Testing;
using Xunit;

namespace PiSharp.Browser.Tests;

public class BrowserExtensionTests
{
    [Fact]
    public async Task InitializeWhenDisabled_RegistersNoTools()
    {
        var ext = new BrowserExtension(new BrowserOptions(false, new BrowserToolOptions()), new FakeBrowserDriverFactory());
        var api = new FakeExtensionApi();

        await ext.InitializeAsync(api);

        Assert.Empty(api.RegisteredTools);
    }

    [Fact]
    public async Task InitializeWhenEnabled_RegistersBrowserTool()
    {
        var ext = new BrowserExtension(new BrowserOptions(true, new BrowserToolOptions()), new FakeBrowserDriverFactory());
        var api = new FakeExtensionApi();

        await ext.InitializeAsync(api);

        var tool = Assert.Single(api.RegisteredTools);
        Assert.Equal("browser", tool.Name);
        Assert.Equal("Browser", tool.Label);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        Assert.Equal("action", Assert.Single(tool.ParametersSchema.GetProperty("required").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task RegisteredTool_Open_SurfacesPageSummary()
    {
        var factory = new FakeBrowserDriverFactory();
        var ext = new BrowserExtension(new BrowserOptions(true, new BrowserToolOptions()), factory);
        var api = new FakeExtensionApi();
        await ext.InitializeAsync(api);

        var tool = Assert.Single(api.RegisteredTools);
        var parameters = JsonSerializer.Deserialize<JsonElement>("""{"action":"open","url":"http://localhost/x"}""");
        var result = await tool.ExecuteAsync("call-1", parameters, CancellationToken.None, null);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("http://localhost/x", text.Text);
        await ext.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ClosesOpenedBrowser()
    {
        var factory = new FakeBrowserDriverFactory();
        var ext = new BrowserExtension(new BrowserOptions(true, new BrowserToolOptions()), factory);
        var api = new FakeExtensionApi();
        await ext.InitializeAsync(api);

        var tool = Assert.Single(api.RegisteredTools);
        await tool.ExecuteAsync("call-1",
            JsonSerializer.Deserialize<JsonElement>("""{"action":"open","url":"http://localhost/x"}"""),
            CancellationToken.None, null);

        Assert.False(factory.Driver.DisposeCalled, "browser stays open until plugin disposal");
        await ext.DisposeAsync();
        Assert.True(factory.Driver.DisposeCalled, "disposal closes the browser");
    }

    [Fact]
    public async Task Dispose_WithoutOpening_IsSafe()
    {
        var ext = new BrowserExtension(new BrowserOptions(true, new BrowserToolOptions()), new FakeBrowserDriverFactory());
        var api = new FakeExtensionApi();
        await ext.InitializeAsync(api);
        await ext.DisposeAsync();
    }
}
