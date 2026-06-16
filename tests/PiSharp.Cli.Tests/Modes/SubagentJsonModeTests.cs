using PiSharp.Cli.Modes;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class SubagentJsonModeTests
{
    [Fact]
    public async Task RunAsyncWritesSessionHeaderLifecycleEventsAndFinalMessages()
    {
        var runtime = await ModeTestRuntime.CreateAsync(ModeTestRuntime.FakeStream("final text"));
        var console = new TestConsoleIO();

        var exitCode = await SubagentJsonMode.RunAsync(
            runtime,
            new SubagentJsonModeOptions(Messages: ["hello"]),
            console,
            CancellationToken.None);

        var output = console.Output.ToString();

        Assert.Equal(0, exitCode);
        Assert.StartsWith("{\"type\":\"session\"", output, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"agent_start\"", output);
        Assert.Contains("\"type\":\"turn_start\"", output);
        Assert.Contains("\"type\":\"message_end\"", output);
        Assert.Contains("\"type\":\"agent_end\"", output);
        Assert.Contains("final text", output);
        Assert.Empty(console.ErrorOutput.ToString());
    }
}
