using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Loops;
using Xunit;

namespace PiSharp.Agent.Tests.Loops;

public sealed class AgentLoopTests
{
    private const long StreamingTextDeltaAllocationBudgetBytes = 8L * 1024 * 1024;

    [Fact]
    public async Task RunEmitsPromptThenAssistantLifecycle()
    {
        var events = new List<string>();
        var context = new AgentContext("system", [], []);
        var config = Config(_ => Stream(new AssistantMessage([new TextContent("ok")], StopReason: "stop")));
        var messages = await AgentLoop.RunAgentLoopAsync([AgentMessages.User("hello")], context, config, e => events.Add(e.GetType().Name));
        Assert.Equal(["AgentStart", "TurnStart", "MessageStart", "MessageEnd", "MessageStart", "MessageEnd", "TurnEnd", "AgentEnd"], events);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void ContinueRejectsAssistantTail()
    {
        var context = new AgentContext("system", [AgentMessages.Assistant("done")], []);
        Assert.Throws<InvalidOperationException>(() => AgentLoop.RunContinue(context, Config(_ => Stream(new AssistantMessage([])))));
    }

    [Fact]
    public async Task ShouldStopRunsBeforeSteeringDrain()
    {
        var steeringDrained = false;
        var config = Config(_ => Stream(new AssistantMessage([new TextContent("ok")], StopReason: "stop"))) with
        {
            ShouldStopAfterTurn = (_, _) => Task.FromResult(true),
            GetSteeringMessages = _ =>
            {
                steeringDrained = true;
                return Task.FromResult<IReadOnlyList<AgentMessage>>([AgentMessages.User("late")]);
            }
        };

        await AgentLoop.RunAgentLoopAsync([AgentMessages.User("hello")], new AgentContext("system", [], []), config, _ => { });
        Assert.False(steeringDrained);
    }

    [Fact]
    public async Task RunAppliesTextDeltasByContentIndex()
    {
        var updates = new List<AssistantMessage>();
        var final = new AssistantMessage([new TextContent("ok")], StopReason: "stop");
        var config = Config(_ => StreamTextDeltas(final));

        var messages = await AgentLoop.RunAgentLoopAsync(
            [AgentMessages.User("hello")],
            new AgentContext("system", [], []),
            config,
            e =>
            {
                if (e is AgentEvent.MessageUpdate update && update.Message is AssistantMessage assistant) updates.Add(assistant);
            });

        var assistant = Assert.IsType<AssistantMessage>(messages[^1]);
        var text = Assert.IsType<TextContent>(assistant.Content[0]);
        Assert.Equal("ok", text.Text);
        Assert.Contains(updates, update => update.Content.OfType<TextContent>().Any(content => content.Text == "o"));
        Assert.Contains(updates, update => update.Content.OfType<TextContent>().Any(content => content.Text == "ok"));
    }

    [Fact]
    public async Task RunAppliesThinkingAndToolCallDeltas()
    {
        using var args = System.Text.Json.JsonDocument.Parse("{\"value\":1}");
        var toolCall = new ToolCallContent("tool-1", "echo", args.RootElement.Clone());
        var final = new AssistantMessage([
            new ThinkingContent("plan"),
            new ToolCallContent("tool-1", "echo", args.RootElement.Clone())
        ], StopReason: "error");
        var config = Config(_ => StreamThinkingAndToolDeltas(final, toolCall));

        var messages = await AgentLoop.RunAgentLoopAsync([AgentMessages.User("hello")], new AgentContext("system", [], []), config, _ => { });

        var assistant = Assert.IsType<AssistantMessage>(messages[^1]);
        var thinking = Assert.IsType<ThinkingContent>(assistant.Content[0]);
        var emittedToolCall = Assert.IsType<ToolCallContent>(assistant.Content[1]);
        Assert.Equal("plan", thinking.Thinking);
        Assert.Equal("tool-1", emittedToolCall.Id);
        Assert.Equal("echo", emittedToolCall.Name);
    }

    [Fact]
    public async Task AgentLoopStreamingTextDeltasPreserveMessageUpdateContract()
    {
        var events = new List<AgentEvent>();
        var streamEvents = BuildTextDeltaEvents(64);
        var config = Config(_ => StreamEvents(streamEvents));

        var messages = await AgentLoop.RunAgentLoopAsync(
            [AgentMessages.User("hello")],
            new AgentContext("system", [], []),
            config,
            events.Add);

        var messageUpdates = events.OfType<AgentEvent.MessageUpdate>().ToArray();
        var textDeltaUpdates = messageUpdates
            .Where(update => update.AssistantMessageEvent is AssistantMessageEvent.TextDelta)
            .ToArray();
        Assert.Equal(64, textDeltaUpdates.Length);
        Assert.Equal(2, events.OfType<AgentEvent.MessageStart>().Count());
        Assert.Equal(2, events.OfType<AgentEvent.MessageEnd>().Count());
        var assistant = Assert.IsType<AssistantMessage>(messages[^1]);
        var finalText = Assert.IsType<TextContent>(assistant.Content[0]);
        Assert.Equal(new string('x', 64), finalText.Text);
        var lastUpdateMessage = Assert.IsType<AssistantMessage>(textDeltaUpdates[^1].Message);
        Assert.Equal(new string('x', 64), Assert.IsType<TextContent>(lastUpdateMessage.Content[0]).Text);
        Assert.All(textDeltaUpdates, update =>
        {
            var delta = Assert.IsType<AssistantMessageEvent.TextDelta>(update.AssistantMessageEvent);
            Assert.Equal("x", delta.Delta);
        });
    }

    [Fact]
    public async Task AgentLoopStreamingTextDeltasStayWithinAllocationBudget()
    {
        var streamEvents = BuildTextDeltaEvents(10_000);
        var config = Config(_ => StreamEvents(streamEvents));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        await AgentLoop.RunAgentLoopAsync(
            [AgentMessages.User("hello")],
            new AgentContext("system", [], []),
            config,
            _ => { });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < StreamingTextDeltaAllocationBudgetBytes, $"Streaming text deltas allocated {allocated:n0} bytes; budget is {StreamingTextDeltaAllocationBudgetBytes:n0} bytes.");
    }

    private static AgentLoopConfig Config(Func<AgentContext, IAsyncEnumerable<AssistantMessageEvent>> stream)
    {
        AgentStreamAsync streamDelegate = (_, context, _, _) => stream(context);
        return new AgentLoopConfig(new ModelDescriptor("test", "test/model", "test"), streamDelegate);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> Stream(AssistantMessage message)
    {
        yield return new AssistantMessageEvent.Start(message);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(message);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamTextDeltas(AssistantMessage final)
    {
        var partial = new AssistantMessage([]);
        yield return new AssistantMessageEvent.Start(partial);
        await Task.Yield();
        yield return new AssistantMessageEvent.TextStart(new AssistantMessage([new TextContent(string.Empty)]), 0);
        yield return new AssistantMessageEvent.TextDelta(new AssistantMessage([new TextContent("o")]), 0, "o");
        yield return new AssistantMessageEvent.TextDelta(new AssistantMessage([new TextContent("ok")]), 0, "k");
        yield return new AssistantMessageEvent.TextEnd(final, 0);
        yield return new AssistantMessageEvent.Done(final, "stop");
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamThinkingAndToolDeltas(AssistantMessage final, ToolCallContent toolCall)
    {
        yield return new AssistantMessageEvent.Start(new AssistantMessage([]));
        await Task.Yield();
        yield return new AssistantMessageEvent.ThinkingStart(new AssistantMessage([new ThinkingContent(string.Empty)]), 0);
        yield return new AssistantMessageEvent.ThinkingDelta(new AssistantMessage([new ThinkingContent("pl")]), 0, "pl");
        yield return new AssistantMessageEvent.ThinkingDelta(new AssistantMessage([new ThinkingContent("plan")]), 0, "an");
        yield return new AssistantMessageEvent.ThinkingEnd(new AssistantMessage([new ThinkingContent("plan")]), 0);
        yield return new AssistantMessageEvent.ToolCallStart(new AssistantMessage([new ThinkingContent("plan"), toolCall]), 1);
        yield return new AssistantMessageEvent.ToolCallDelta(new AssistantMessage([new ThinkingContent("plan"), toolCall]), 1, "{\"value\":1}");
        yield return new AssistantMessageEvent.ToolCallEnd(new AssistantMessage([new ThinkingContent("plan"), toolCall]), 1, toolCall);
        yield return new AssistantMessageEvent.Done(final, "error");
    }

    private static AssistantMessageEvent[] BuildTextDeltaEvents(int count)
    {
        var events = new List<AssistantMessageEvent>(count + 4)
        {
            new AssistantMessageEvent.Start(new AssistantMessage([])),
            new AssistantMessageEvent.TextStart(new AssistantMessage([new TextContent(string.Empty)]), 0)
        };
        var text = string.Empty;
        for (var index = 0; index < count; index++)
        {
            text += "x";
            events.Add(new AssistantMessageEvent.TextDelta(new AssistantMessage([new TextContent(text)]), 0, "x"));
        }

        var final = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        events.Add(new AssistantMessageEvent.TextEnd(final, 0));
        events.Add(new AssistantMessageEvent.Done(final, "stop"));
        return events.ToArray();
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamEvents(IReadOnlyList<AssistantMessageEvent> events)
    {
        await Task.CompletedTask;
        foreach (var evt in events) yield return evt;
    }
}
