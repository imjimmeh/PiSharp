using System.Text.Json;
using PiSharp.Plugins.Debug;
using Xunit;

namespace PiSharp.Plugins.Lsp.Tests;

/// <summary>
/// <see cref="DebugAdapterConfigParser"/>: validation and the <c>${cwd}</c>/<c>${path}</c>
/// interpolation of the attach body.
/// </summary>
public sealed class DebugAdapterConfigTests
{
    [Fact]
    public void ParseAcceptsFullConfig()
    {
        var section = JsonDocument.Parse(
                """{"command":["debugpy","--listen","5678"],"extensions":[".py"],"env":{"PYTHONUNBUFFERED":"1"},"workingDirectory":"C:\\proj","attach":{"request":"attach","host":"127.0.0.1","port":5678},"timeoutMs":5000}""")
            .RootElement;

        var result = DebugAdapterConfigParser.Parse(section, "python");

        Assert.True(result.IsOk);
        var config = result.Value;
        Assert.Equal(new[] { "debugpy", "--listen", "5678" }, config.Command);
        Assert.Equal(new[] { ".py" }, config.Extensions);
        Assert.Equal("C:\\proj", config.WorkingDirectory);
        Assert.Equal("1", config.Env!["PYTHONUNBUFFERED"]!.ToString());
        Assert.Equal(5000, config.TimeoutMs);
        Assert.Equal("attach", config.Attach!.Value.GetProperty("request").GetString());
    }

    [Fact]
    public void ParseDefaultsTimeoutAndAttach()
    {
        var section = JsonDocument.Parse("""{"command":["dlv-dap"],"extensions":[".go"]}""").RootElement;

        var result = DebugAdapterConfigParser.Parse(section, "go");

        Assert.True(result.IsOk);
        Assert.Equal(10000, result.Value.TimeoutMs);
        Assert.Null(result.Value.Attach);
    }

    [Theory]
    [InlineData("""{"command":[]}""", "non-empty 'command'")]
    [InlineData("""{"extensions":[".py"]}""", "non-empty 'command'")]
    [InlineData("""{"command":["x"],"extensions":[".py"],"timeoutMs":-3}""", "timeoutMs")]
    [InlineData("42", "must be a JSON object")]
    [InlineData("[]", "must be a JSON object")]
    public void ParseRejectsInvalidConfig(string json, string expectedErrorFragment)
    {
        var section = JsonDocument.Parse(json).RootElement;

        var result = DebugAdapterConfigParser.Parse(section, "python");

        Assert.True(result.IsErr);
        Assert.Contains(expectedErrorFragment, result.Error);
    }

    [Fact]
    public void InterpolateReplacesCwdAndPathInNestedStrings()
    {
        var attach = JsonDocument.Parse(
                """{"request":"launch","program":"${path}","pathMappings":[{"localRoot":"${cwd}","remoteRoot":"${cwd}"}],"port":5678}""")
            .RootElement;

        var interpolated = DebugAdapterConfigParser.Interpolate(attach, @"C:\work\demo", @"C:\work\demo\main.go");

        Assert.Equal(@"C:\work\demo\main.go", interpolated.GetProperty("program").GetString());
        var mapping = interpolated.GetProperty("pathMappings")[0];
        Assert.Equal(@"C:\work\demo", mapping.GetProperty("localRoot").GetString());
        Assert.Equal(@"C:\work\demo", mapping.GetProperty("remoteRoot").GetString());
        Assert.Equal(5678, interpolated.GetProperty("port").GetInt32());
    }

    [Fact]
    public void InterpolateWithNullPathLeavesPathPlaceholderAndReplacesCwd()
    {
        var attach = JsonDocument.Parse(
                """{"request":"attach","pathMappings":[{"localRoot":"${cwd}","remoteRoot":"${cwd}"}],"program":"${path}"}""")
            .RootElement;

        var interpolated = DebugAdapterConfigParser.Interpolate(attach, @"C:\work\demo", path: null);

        var mapping = interpolated.GetProperty("pathMappings")[0];
        Assert.Equal(@"C:\work\demo", mapping.GetProperty("localRoot").GetString());
        Assert.Equal("${path}", interpolated.GetProperty("program").GetString());
    }

    [Fact]
    public void InterpolateLeavesUnrelatedStringsUntouched()
    {
        var attach = JsonDocument.Parse("""{"request":"attach","note":"no placeholders here"}""").RootElement;

        var interpolated = DebugAdapterConfigParser.Interpolate(attach, @"C:\work\demo", @"C:\work\demo\x.py");

        Assert.Equal("no placeholders here", interpolated.GetProperty("note").GetString());
    }
}
