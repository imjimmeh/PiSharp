using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Ai.Providers.Faux;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Agent.Tests;

/// <summary>
/// Phase 2 (P10) integration tests for the TTSR retry loop, exercised through the
/// in-process harness with a scripted <see cref="FauxProvider"/> stream and a fake
/// <see cref="IStreamDeltaInterceptor"/> registered into the extension registry.
/// </summary>
public sealed class AgentLoopStreamRuleTests
{
    private const string Reminder = "no-todo rule body";
    private const string RetryReason = "rule:no-todo";

    [Fact]
    public async Task RetryDecisionRestartsStreamWithReminderAndEmitsAutoRetryEvents()
    {
        var session = CreateSession();
        var interceptor = new MatchingInterceptor("todo list", new StreamDeltaDecision(StreamDeltaAction.Retry, Reminder, RetryReason));
        var registry = new ExtensionRegistry();
        using var _reg = registry.RegisterStreamDeltaInterceptor("test-rules", interceptor);
        var observedContexts = new List<AgentContext>();
        var streamCalls = 0;
        AgentStreamAsync stream = (model, context, options, ct) =>
        {
            observedContexts.Add(context);
            streamCalls++;
            IReadOnlyList<FauxResponseItem> items = streamCalls == 1
                ? [FauxResponseItem.Text("todo list")]
                : [FauxResponseItem.Text("no todos remain")];
            return new FauxProvider(items).StreamAsync(model, context, options, ct);
        };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, Model, stream, FakeCompletion, [], Extensions: registry));
        var events = new List<AgentHarnessEvent>();
        using var _sub = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        var result = await harness.PromptAsync("hello");

        Assert.Equal(2, streamCalls);
        Assert.Equal(2, interceptor.PrepareCalls);
        Assert.Equal("no todos remain", result.Content.OfType<TextContent>().FirstOrDefault()?.Text);

        // The retried StreamAsync call receives the reminder in context.Messages
        // (the PrepareStreamMessages output).
        var retried = Assert.IsType<UserMessage>(observedContexts[1].Messages[^1]);
        Assert.Equal(Reminder, Assert.IsType<TextContent>(Assert.Single(retried.Content)).Text);
        var prepared = Assert.IsType<UserMessage>(interceptor.PreparedMessages[1][^1]);
        Assert.Equal(Reminder, Assert.IsType<TextContent>(Assert.Single(prepared.Content)).Text);

        var starts = events.OfType<AgentHarnessEvent.Core>().Select(e => e.Event).OfType<AgentEvent.AutoRetryStart>().ToArray();
        var start = Assert.Single(starts);
        Assert.Equal(1, start.Attempt);
        Assert.Equal(3, start.MaxAttempts);
        Assert.Equal(0, start.DelayMs);
        Assert.Equal(RetryReason, start.ErrorMessage);

        var end = Assert.Single(events.OfType<AgentHarnessEvent.Core>().Select(e => e.Event).OfType<AgentEvent.AutoRetryEnd>());
        Assert.True(end.Success);
        Assert.Equal(1, end.Attempt);
        Assert.Null(end.FinalError);

        // The aborted partial is never persisted; the reminder is never persisted;
        // only the user prompt and the final retried assistant message reach the session.
        var entries = (await session.GetEntriesAsync()).OfType<MessageEntry>().Select(e => e.Message).ToArray();
        Assert.Collection(entries,
            message => Assert.IsType<UserMessage>(message),
            message =>
            {
                var assistant = Assert.IsType<AssistantMessage>(message);
                Assert.Equal("no todos remain", assistant.Content.OfType<TextContent>().FirstOrDefault()?.Text);
            });
    }

    [Fact]
    public async Task AbortDecisionEndsTurnWithErrorStopReason()
    {
        var session = CreateSession();
        var interceptor = new MatchingInterceptor("todo list", new StreamDeltaDecision(StreamDeltaAction.Abort, Reason: RetryReason));
        var registry = new ExtensionRegistry();
        using var _reg = registry.RegisterStreamDeltaInterceptor("test-rules", interceptor);
        var streamCalls = 0;
        AgentStreamAsync stream = (model, context, options, ct) =>
        {
            streamCalls++;
            return new FauxProvider([FauxResponseItem.Text("todo list")]).StreamAsync(model, context, options, ct);
        };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, Model, stream, FakeCompletion, [], Extensions: registry));
        var events = new List<AgentHarnessEvent>();
        using var _sub = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        var result = await harness.PromptAsync("hello");

        Assert.Equal(1, streamCalls);
        Assert.Equal("error", result.StopReason);
        Assert.Equal(RetryReason, result.ErrorMessage);
        Assert.Equal($"Stream aborted: {RetryReason}", result.Content.OfType<TextContent>().FirstOrDefault()?.Text);
        Assert.DoesNotContain(events, evt => evt is AgentHarnessEvent.Core { Event: AgentEvent.AutoRetryStart or AgentEvent.AutoRetryEnd });

        var entries = (await session.GetEntriesAsync()).OfType<MessageEntry>().Select(e => e.Message).ToArray();
        Assert.Collection(entries,
            message => Assert.IsType<UserMessage>(message),
            message =>
            {
                var assistant = Assert.IsType<AssistantMessage>(message);
                Assert.Equal("error", assistant.StopReason);
                Assert.Equal(RetryReason, assistant.ErrorMessage);
            });
    }

    [Fact]
    public async Task RetryCapExceededEmitsAutoRetryEndWithFailure()
    {
        var session = CreateSession();
        var interceptor = new MatchingInterceptor("todo list", new StreamDeltaDecision(StreamDeltaAction.Retry, Reminder, RetryReason));
        var registry = new ExtensionRegistry();
        using var _reg = registry.RegisterStreamDeltaInterceptor("test-rules", interceptor);
        var streamCalls = 0;
        AgentStreamAsync stream = (model, context, options, ct) =>
        {
            streamCalls++;
            return new FauxProvider([FauxResponseItem.Text("todo list")]).StreamAsync(model, context, options, ct);
        };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, Model, stream, FakeCompletion, [], Extensions: registry));
        var events = new List<AgentHarnessEvent>();
        using var _sub = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        var result = await harness.PromptAsync("hello");

        // Default MaxStreamRetries = 3: initial attempt + 3 retries, then failure.
        Assert.Equal(4, streamCalls);
        Assert.Equal("error", result.StopReason);

        var starts = events.OfType<AgentHarnessEvent.Core>().Select(e => e.Event).OfType<AgentEvent.AutoRetryStart>().ToArray();
        Assert.Equal([1, 2, 3], starts.Select(s => s.Attempt));
        Assert.All(starts, start => Assert.Equal(3, start.MaxAttempts));
        Assert.All(starts, start => Assert.Equal(RetryReason, start.ErrorMessage));

        var end = Assert.Single(events.OfType<AgentHarnessEvent.Core>().Select(e => e.Event).OfType<AgentEvent.AutoRetryEnd>());
        Assert.False(end.Success);
        Assert.Equal(3, end.Attempt);
        Assert.Equal($"Max stream retries exceeded for {RetryReason}", end.FinalError);

        var entries = (await session.GetEntriesAsync()).OfType<MessageEntry>().Select(e => e.Message).ToArray();
        Assert.Collection(entries,
            message => Assert.IsType<UserMessage>(message),
            message =>
            {
                var assistant = Assert.IsType<AssistantMessage>(message);
                Assert.Equal("error", assistant.StopReason);
                Assert.Equal($"Max stream retries exceeded for {RetryReason}", assistant.ErrorMessage);
            });
    }

    [Fact]
    public async Task NoInterceptorsStreamsIdenticallyToTheDefaultPath()
    {
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, Model, FauxStream("ok"), FakeCompletion, []));
        var events = new List<AgentHarnessEvent>();
        using var _sub = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        var result = await harness.PromptAsync("hello");

        Assert.Equal("ok", result.Content.OfType<TextContent>().FirstOrDefault()?.Text);

        var core = events.OfType<AgentHarnessEvent.Core>().Select(e => Label(e.Event)).ToArray();
        Assert.Equal(
        [
            "agent_start",
            "turn_start",
            "message_start",
            "message_end",
            "message_start",
            "message_update",
            "message_update",
            "message_update",
            "message_end",
            "turn_end",
            "agent_end"
        ], core);
        Assert.DoesNotContain(events, evt => evt is AgentHarnessEvent.Core { Event: AgentEvent.AutoRetryStart or AgentEvent.AutoRetryEnd });

        var entries = (await session.GetEntriesAsync()).OfType<MessageEntry>().Select(e => e.Message).ToArray();
        Assert.Collection(entries,
            message => Assert.IsType<UserMessage>(message),
            message => Assert.IsType<AssistantMessage>(message));
    }

    private static readonly ModelDescriptor Model = new("test", "test", "test");

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl")));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("summarized"));

    private static AgentStreamAsync FauxStream(string text)
    {
        AgentStreamAsync stream = (model, context, options, ct) =>
            new FauxProvider([FauxResponseItem.Text(text)]).StreamAsync(model, context, options, ct);
        return stream;
    }

    private static string Label(AgentEvent evt) => evt switch
    {
        AgentEvent.AgentStart => "agent_start",
        AgentEvent.AgentEnd => "agent_end",
        AgentEvent.TurnStart => "turn_start",
        AgentEvent.TurnEnd => "turn_end",
        AgentEvent.MessageStart => "message_start",
        AgentEvent.MessageUpdate => "message_update",
        AgentEvent.MessageEnd => "message_end",
        AgentEvent.AutoRetryStart => "auto_retry_start",
        AgentEvent.AutoRetryEnd => "auto_retry_end",
        _ => "other"
    };

    /// <summary>
    /// Returns <paramref name="decision"/> when the visible text accumulated so far
    /// contains <paramref name="pattern"/>; otherwise continues.
    /// </summary>
    private sealed class MatchingInterceptor(string pattern, StreamDeltaDecision decision) : IStreamDeltaInterceptor
    {
        public int PrepareCalls { get; private set; }
        public List<AgentMessage[]> PreparedMessages { get; } = [];

        public Task<StreamDeltaDecision?> InterceptDeltaAsync(StreamDeltaContext context, CancellationToken cancellationToken = default)
        {
            var text = string.Concat(context.Partial.Content.OfType<TextContent>().Select(t => t.Text));
            return Task.FromResult<StreamDeltaDecision?>(text.Contains(pattern, StringComparison.Ordinal) ? decision : null);
        }

        public Task<IReadOnlyList<AgentMessage>> PrepareMessagesAsync(
            IReadOnlyList<AgentMessage> messages,
            AgentContext context,
            CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            PreparedMessages.Add(messages.ToArray());
            return Task.FromResult(messages);
        }
    }
}
