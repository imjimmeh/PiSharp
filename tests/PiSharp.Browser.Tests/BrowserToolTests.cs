using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Browser.Runtime;
using PiSharp.Browser.Tests.Fakes;
using PiSharp.Browser.Tools;
using Xunit;

namespace PiSharp.Browser.Tests;

public class BrowserToolTests
{
    private static readonly BrowserToolOptions Options = new();

    private static JsonElement Params(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>A single shared <see cref="BrowserSession"/> (as in production) so actions stack on one tab.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public FakeBrowserDriverFactory Factory { get; } = new();
        public BrowserSession Session { get; }

        public Harness()
        {
            Session = new BrowserSession(Factory, Options);
        }

        public Task<AgentToolResult<object?>> Exc(JsonElement parameters)
            => BrowserTool.ExecuteAsync("call-1", parameters, CancellationToken.None, null, Session, Options);

        public async ValueTask DisposeAsync() => await Session.DisposeAsync();
    }

    [Fact]
    public void BuildParametersSchema_IsValidJsonObject_RequiresAction()
    {
        var schema = BrowserTool.BuildParametersSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("action", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        // additionalProperties must be false so the model cannot sneak in extra params.
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        var action = schema.GetProperty("properties").GetProperty("action");
        var enumValues = action.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "open", "run", "screenshot", "observe" }, enumValues);
    }

    [Theory]
    [InlineData("""{"action":"frob"}""")]
    [InlineData("""{"action":"open"}""")] // missing required url
    [InlineData("""{"action":"run"}""")] // missing required script
    public async Task ExecuteAsync_InvalidParameters_ReturnsError(string json)
    {
        await using var h = new Harness();
        var result = await h.Exc(Params(json));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.StartsWith("Error:", text.Text);
    }

    [Fact]
    public async Task Open_NavigatesAndSummarizes()
    {
        await using var h = new Harness();
        var result = await h.Exc(Params("""{"action":"open","url":"http://localhost/test"}"""));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("http://localhost/test", text.Text);
        Assert.Contains("Open Title", text.Text);

        var details = Assert.IsType<BrowserToolDetails>(result.Details);
        Assert.Equal("open", details.Action);
        Assert.Equal("http://localhost/test", details.Url);
        Assert.Equal("Open Title", details.Title);
    }

    [Fact]
    public async Task Open_CreatesDriverOnce()
    {
        await using var h = new Harness();
        await h.Exc(Params("""{"action":"open","url":"http://localhost/","waitFor":"#app","timeoutMs":5000}"""));
        await h.Exc(Params("""{"action":"open","url":"http://localhost/2"}"""));
        Assert.Equal(1, h.Factory.CreateCount);
    }

    [Fact]
    public async Task Run_ReturnsSerializedJsResult()
    {
        await using var h = new Harness();
        h.Factory.Driver.ScriptResult = "42";
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        var result = await h.Exc(Params("""{"action":"run","script":"1 + 41"}"""));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("42", text.Text);
        Assert.Equal("1 + 41", h.Factory.Driver.LastScript);
        Assert.True(h.Factory.Driver.LastReturnByValue);
    }

    [Fact]
    public async Task Run_PassesReturnByValueFlagWhenFalse()
    {
        await using var h = new Harness();
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        await h.Exc(Params("""{"action":"run","script":"fetch('/')","returnByValue":false}"""));
        Assert.False(h.Factory.Driver.LastReturnByValue);
    }

    [Fact]
    public async Task Screenshot_ReturnsPngImageAttachment()
    {
        await using var h = new Harness();
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        var result = await h.Exc(Params("""{"action":"screenshot"}"""));

        Assert.Equal(2, result.Content.Count);
        var note = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Contains("Captured screenshot", note.Text);

        var image = Assert.IsType<ImageContent>(result.Content[1]);
        Assert.Equal("image/png", image.MediaType);
        var bytes = Convert.FromBase64String(image.Data);
        // PNG signature: 89 50 4E 47
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'N', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);

        var details = Assert.IsType<BrowserToolDetails>(result.Details);
        Assert.Equal("screenshot", details.Action);
        Assert.Equal("image/png", details.MimeType);
    }

    [Fact]
    public async Task Screenshot_ForwardsFullPage()
    {
        await using var h = new Harness();
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        await h.Exc(Params("""{"action":"screenshot","fullPage":true}"""));
        Assert.True(h.Factory.Driver.LastFullPage);
    }

    [Fact]
    public async Task Observe_ReturnsAccessibilitySnapshot()
    {
        await using var h = new Harness();
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        var result = await h.Exc(Params("""{"action":"observe"}"""));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("test page", text.Text, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<BrowserToolDetails>(result.Details);
    }

    [Fact]
    public async Task Run_WhenBrowserNotOpen_ReturnsError()
    {
        await using var h = new Harness();
        h.Factory.Driver.IsOpen = false;
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        var result = await h.Exc(Params("""{"action":"run","script":"1"}"""));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("not open", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScriptError_SurfacesAsToolError()
    {
        await using var h = new Harness();
        await h.Exc(Params("""{"action":"open","url":"http://localhost/"}"""));
        h.Factory.Driver.RunException = new InvalidOperationException("SyntaxError: Unexpected token");
        var result = await h.Exc(Params("""{"action":"run","script":"oops("}"""));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("SyntaxError", text.Text);
    }
}
