using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiSharp.Cli.Logging;
using Xunit;

namespace PiSharp.Cli.Tests.Logging;

/// <summary>
/// P25 C5: the JSON-lines file formatter — structured state keys serialized verbatim, template
/// message rendering, exception serialization, non-JSON values falling back to ToString, and the
/// <c>logging.json</c> / <c>PISHARP_LOG_FORMAT</c> resolution precedence.
/// </summary>
public sealed class JsonLogFormatterTests
{
    [Fact]
    public void Format_SerializesStructuredStateKeysAndRenderedMessage()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("tool", "bash"),
            new("sessionId", "srv_123"),
            new("{OriginalFormat}", "tool {tool} ran in session {sessionId}"),
        };
        var line = JsonLogFormatter.Format("PiSharp.Tools", LogLevel.Information, new EventId(7, "tool_started"), state, null,
            (s, _) => "tool bash ran in session srv_123");

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal("PiSharp.Tools", root.GetProperty("category").GetString());
        Assert.Equal("tool bash ran in session srv_123", root.GetProperty("message").GetString());
        Assert.Equal("bash", root.GetProperty("state").GetProperty("tool").GetString());
        Assert.Equal("srv_123", root.GetProperty("state").GetProperty("sessionId").GetString());
        Assert.False(root.GetProperty("state").TryGetProperty("{OriginalFormat}", out _));
        Assert.Equal(7, root.GetProperty("eventId").GetProperty("id").GetInt32());
        Assert.Equal("tool_started", root.GetProperty("eventId").GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("ts", out _));
    }

    [Fact]
    public void Format_OmitsEventIdWhenUnset()
    {
        var line = JsonLogFormatter.Format("cat", LogLevel.Debug, default, new List<KeyValuePair<string, object?>>(), null,
            (_, _) => "plain");

        using var document = JsonDocument.Parse(line);
        Assert.False(document.RootElement.TryGetProperty("eventId", out _));
    }

    [Fact]
    public void Format_SerializesException()
    {
        var exception = new InvalidOperationException("boom");
        var line = JsonLogFormatter.Format("cat", LogLevel.Error, default, new List<KeyValuePair<string, object?>>(), exception,
            (_, _) => "failed");

        using var document = JsonDocument.Parse(line);
        var ex = document.RootElement.GetProperty("exception");
        Assert.Equal("System.InvalidOperationException", ex.GetProperty("type").GetString());
        Assert.Equal("boom", ex.GetProperty("message").GetString());
        Assert.Contains("InvalidOperationException", ex.GetProperty("stackTrace").GetString());
    }

    [Fact]
    public void Format_NonJsonValueFallsBackToString()
    {
        var state = new List<KeyValuePair<string, object?>> { new("payload", new Uri("https://example.com/x")) };
        var line = JsonLogFormatter.Format("cat", LogLevel.Information, default, state, null, (_, _) => "m");

        using var document = JsonDocument.Parse(line);
        Assert.Equal("https://example.com/x", document.RootElement.GetProperty("state").GetProperty("payload").GetString());
    }

    [Fact]
    public void Format_PlainStateWithoutPairsStillRendersMessage()
    {
        var line = JsonLogFormatter.Format("cat", LogLevel.Warning, default, "scalar-state", null, (_, _) => "scalar message");

        using var document = JsonDocument.Parse(line);
        Assert.Equal("scalar message", document.RootElement.GetProperty("message").GetString());
        Assert.False(document.RootElement.TryGetProperty("state", out _));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, "json", true)]
    [InlineData(false, "JSON", true)]
    [InlineData(true, "text", false)]
    [InlineData(false, null, false)]
    public void ResolveJsonFormat_EnvOverridesSettings(bool settingsJson, string? envFormat, bool expected)
        => Assert.Equal(expected, CliFileLogging.ResolveJsonFormat(settingsJson, envFormat));

    [Fact]
    public void JsonFileLoggerProvider_WritesJsonLinesWithNamedState()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "app.log");
        using var provider = new JsonFileLoggerProvider(new RollingFileLoggerOptions(path, LogLevel.Debug, MaxRetainedFiles: 3, Mode: RollingFileMode.ExactFile, Json: true));
        var logger = provider.CreateLogger("PiSharp.Test");

        logger.LogInformation("tool {Tool} finished in {SessionId}", "bash", "srv_1");

        provider.Dispose();

        var line = File.ReadAllLines(path).Single();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal("PiSharp.Test", root.GetProperty("category").GetString());
        Assert.Equal("tool bash finished in srv_1", root.GetProperty("message").GetString());
        Assert.Equal("bash", root.GetProperty("state").GetProperty("Tool").GetString());
        Assert.Equal("srv_1", root.GetProperty("state").GetProperty("SessionId").GetString());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-jsonlog-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}
