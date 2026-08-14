using System.Text.Json;
using ModelContextProtocol.Protocol;
using PiSharp.Tools.Shared;
using Xunit;

namespace PiSharp.Mcp.Tests;

public sealed class McpResultFormatterTests
{
    [Fact]
    public void Format_JoinsTextBlocks()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "line one" },
                new TextContentBlock { Text = "line two" }
            ]
        };
        Assert.Equal("line one\nline two", McpResultFormatter.Format(result, "fileserver"));
    }

    [Fact]
    public void Format_AppendsErrorPrefixForIsError()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "boom" }] };
        Assert.Equal("[MCP error from fileserver] boom", McpResultFormatter.Format(result, "fileserver"));
    }

    [Fact]
    public void Format_IncludesStructuredContent()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.Deserialize<JsonElement>("""{"count":3}""")
        };
        Assert.Equal("""{"count":3}""", McpResultFormatter.Format(result, "fileserver"));
    }

    [Fact]
    public void Format_PlacesImageBlocksAsPlaceholders()
    {
        var result = new CallToolResult
        {
            Content = [new ImageContentBlock { MimeType = "image/png", Data = new byte[4] }]
        };
        Assert.Contains("[MCP image content block", McpResultFormatter.Format(result, "fileserver"));
    }

    [Fact]
    public void Format_TruncatesLongOutput()
    {
        var text = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"line {i}"));
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        var options = new TruncationOptions(5, 50 * 1024);
        var formatted = McpResultFormatter.Format(result, "fileserver", options);
        Assert.Contains("Output truncated", formatted);
    }

    [Fact]
    public void Format_EmptyResultBecomesNoContent()
    {
        Assert.Equal("(no content)", McpResultFormatter.Format(new CallToolResult(), "fileserver"));
    }
}
