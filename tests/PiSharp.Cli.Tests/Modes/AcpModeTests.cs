using System.Text.Json;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Parsing;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class AcpModeTests
{
    [Fact]
    public async Task Initialize_RespondsWithProtocolVersion()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO(Msg(1, "initialize"));

        var exitCode = await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        Assert.Equal(0, exitCode);
        using var response = FirstResponse(console);
        Assert.Equal(1, response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task SessionNew_RespondsWithSessionId()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var cwdJson = JsonSerializer.Serialize(runtime.Session.Metadata.Cwd);
        var input =
            Msg(1, "initialize") +
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{{\"cwd\":{cwdJson}}}}}" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        var lines = Lines(console);
        using var sessionNew = JsonDocument.Parse(lines[1]);
        Assert.StartsWith("sess_", sessionNew.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task SessionPrompt_RespondsWithStopReason()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var cwdJson = JsonSerializer.Serialize(runtime.Session.Metadata.Cwd);
        var input =
            Msg(1, "initialize") +
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{{\"cwd\":{cwdJson}}}}}" + Environment.NewLine +
            $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/prompt\",\"params\":{{\"prompt\":[{{\"type\":\"text\",\"text\":\"hello acp\"}}]}}}}" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        var lines = Lines(console);
        using var promptResponse = JsonDocument.Parse(lines[2]);
        Assert.Equal("end_turn", promptResponse.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO(Msg(1, "bogus/method"));

        await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        using var response = FirstResponse(console);
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task MalformedJson_ReturnsParseError()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO("not valid json" + Environment.NewLine);

        await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        using var response = FirstResponse(console);
        Assert.Equal(-32700, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SessionNew_CwdMismatch_ReturnsInvalidParams()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var input = Msg(1, "initialize") +
                    $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{{\"cwd\":\"C:\\\\wrong\\\\dir\"}}}}" + Environment.NewLine;
        var console = new TestConsoleIO(input);

        await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        var lines = Lines(console);
        using var sessionNew = JsonDocument.Parse(lines[1]);
        Assert.Equal(-32602, sessionNew.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task SessionPrompt_WithNoActiveSession_ReturnsNoActiveSession()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var console = new TestConsoleIO(
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session/prompt\",\"params\":{{\"prompt\":[{{\"type\":\"text\",\"text\":\"hi\"}}]}}}}" + Environment.NewLine);

        await AcpMode.RunAsync(runtime, console, AcpApprovalMode.Ask);

        using var response = FirstResponse(console);
        Assert.Equal(-32000, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static string Msg(int id, string method)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{{}}}}" + Environment.NewLine;

    private static string[] Lines(TestConsoleIO console)
        => console.Output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static JsonDocument FirstResponse(TestConsoleIO console)
        => JsonDocument.Parse(Lines(console)[0]);
}
