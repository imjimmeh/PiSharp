using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Cli.Modes;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class PrintModeTests
{
    [Fact]
    public async Task TextModePrintsFinalAssistantTextOnly()
    {
        var runtime = await ModeTestRuntime.CreateAsync(ModeTestRuntime.FakeStream("final text"));
        var console = new TestConsoleIO();

        var exitCode = await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Text, Messages: ["hello"]), console);

        Assert.Equal(0, exitCode);
        Assert.Equal("final text" + Environment.NewLine, console.Output.ToString());
        Assert.Empty(console.ErrorOutput.ToString());
    }

    [Fact]
    public async Task TextModeErrorsGoToStderrAndReturnNonZero()
    {
        AgentStreamAsync stream = (_, _, _, _) => ErrorStream();
        var runtime = await ModeTestRuntime.CreateAsync(stream);
        var console = new TestConsoleIO();

        var exitCode = await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Text, Messages: ["hello"]), console);

        Assert.Equal(1, exitCode);
        Assert.Empty(console.Output.ToString());
        Assert.Contains("boom", console.ErrorOutput.ToString());
    }

    [Fact]
    public async Task TextModeAppliesInputHookBeforePrompt()
    {
        AgentContext? observed = null;
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.Input, (evt, _) => { evt.TransformInput("transformed prompt"); return Task.CompletedTask; });
        var runtime = await ModeTestRuntime.CreateAsync(stream: (_, context, _, _) => { observed = context; return ModeTestRuntime.StreamText("ok"); }, extensionManager: new ExtensionManager(registry));
        var console = new TestConsoleIO();

        var exitCode = await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Text, Messages: ["original"]), console);

        Assert.Equal(0, exitCode);
        var user = Assert.Single(observed!.Messages.OfType<UserMessage>());
        Assert.Equal("transformed prompt", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public async Task JsonModeWritesOnlySerializedEventsToOriginalStdout()
    {
        var runtime = await ModeTestRuntime.CreateAsync(ModeTestRuntime.FakeStream("json text"));
        var console = new TestConsoleIO();

        var exitCode = await PrintMode.RunAsync(runtime, new PrintModeOptions(PrintOutputMode.Json, Messages: ["hello"]), console);

        Assert.Equal(0, exitCode);
        var output = console.Output.ToString();
        Assert.Contains("\"type\":\"core\"", output);
        Assert.Contains("\"type\":\"message_end\"", output);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> ErrorStream()
    {
        await Task.Yield();
        var message = new AssistantMessage([], StopReason: "error", ErrorMessage: "boom");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Error(message, "error");
    }
}
