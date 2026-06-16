using PiSharp.Abstractions.Messages;
using PiSharp.Tools.Bash;
using PiSharp.Tools.Tests.Fakes;
using Xunit;

namespace PiSharp.Tools.Tests;

public sealed class BashToolTests
{
    [Fact]
    public async Task BashToolEmitsPartialUpdateFromByteChunks()
    {
        var env = new FakeExecutionEnv();
        env.EnqueueShellResult("hello\n");
        var tool = new BashTool(env);
        var updates = new List<string>();
        var result = await tool.ExecuteAsync("tc1", new BashToolInput("echo hello"), onUpdate: partial =>
        {
            var text = partial.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(text)) updates.Add(text);
        });
        Assert.Contains(updates, update => update.Contains("hello"));
        Assert.Equal("hello\n", Assert.IsType<TextContent>(result.Content.Single()).Text);
    }

    [Fact]
    public async Task BashToolThrowsWithExitCodeAndOutput()
    {
        var env = new FakeExecutionEnv();
        env.EnqueueShellResult("bad\n", exitCode: 2);
        var tool = new BashTool(env);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("tc1", new BashToolInput("fail")));
        Assert.Contains("bad", error.Message);
        Assert.Contains("Command exited with code 2", error.Message);
    }

    [Fact]
    public void BashToolSchemaUsesTypescriptFieldNames()
    {
        var tool = new BashTool(new FakeExecutionEnv());
        var json = tool.ParametersSchema.GetRawText();
        Assert.Contains("command", json);
        Assert.Contains("timeout", json);
    }
}
