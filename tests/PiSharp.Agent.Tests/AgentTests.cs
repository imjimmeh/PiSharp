using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using Xunit;

namespace PiSharp.Agent.Tests;

public sealed class AgentTests
{
    [Fact]
    public async Task PromptAppendsMessageEndToState()
    {
        var agent = new Agent(new AgentOptions(Config("ok")));
        await agent.PromptAsync("hello");
        Assert.Contains(agent.State.Messages, message => message is UserMessage);
        Assert.Contains(agent.State.Messages, message => message is AssistantMessage);
        Assert.False(agent.State.IsStreaming);
    }

    [Fact]
    public async Task ContinueUsesQueuedFollowUpWhenAssistantTailExists()
    {
        var agent = new Agent(new AgentOptions(Config("ok")));
        await agent.PromptAsync("hello");
        agent.FollowUp(AgentMessages.User("next"));
        await agent.ContinueAsync();
        Assert.True(agent.State.Messages.OfType<UserMessage>().Count() >= 2);
    }

    [Fact]
    public async Task WaitForIdleIncludesListeners()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new Agent(new AgentOptions(Config("ok")));
        agent.Subscribe(async (_, _) => await gate.Task);
        var prompt = agent.PromptAsync("hello");
        var idle = agent.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(prompt, idle);
    }

    private static AgentLoopConfig Config(string text)
    {
        AgentStreamAsync stream = (_, _, _, _) => StreamHelper(text);
        return new AgentLoopConfig(new ModelDescriptor("test", "test/model", "test"), stream);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(message);
    }
}
