using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

namespace PiSharp.Agent.Tests.Harness;

public sealed class AgentHarnessEventTests
{
    [Fact]
    public async Task SubscribeReceivesCoreEventsFromPrompt()
    {
        var session = CreateSession();
        var events = new List<AgentHarnessEvent>();
        var harness = Harness(session, FakeStream("ok"));
        using var _ = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        await harness.PromptAsync("hello");

        Assert.Contains(events, evt => evt is AgentHarnessEvent.Core { Event: AgentEvent.MessageStart });
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Core { Event: AgentEvent.MessageEnd });
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Core { Event: AgentEvent.AgentEnd });
    }

    [Fact]
    public async Task SetModelPersistsAndEmitsModelSelect()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));
        AgentHarnessEvent? observed = null;
        using var _ = harness.Subscribe((evt, _) => { observed = evt; return Task.CompletedTask; });

        await harness.SetModelAsync(new ModelDescriptor("openai", "gpt-4o", "openai-responses"), "test");

        Assert.Equal("openai", harness.Model.Provider);
        var entry = Assert.Single((await session.GetEntriesAsync()).OfType<ModelChangeEntry>());
        Assert.Equal("openai", entry.Provider);
        Assert.Equal("gpt-4o", entry.ModelId);
        Assert.IsType<AgentHarnessOwnEvent.ModelSelect>(Assert.IsType<AgentHarnessEvent.Own>(observed).Event);
    }

    [Fact]
    public async Task SetThinkingLevelPersistsAndEmitsThinkingLevelSelect()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));
        var events = new List<AgentHarnessEvent>();
        using var _ = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        await harness.SetThinkingLevelAsync(ThinkingLevel.High);

        Assert.Equal(ThinkingLevel.High, harness.ThinkingLevel);
        var entry = Assert.Single((await session.GetEntriesAsync()).OfType<ThinkingLevelChangeEntry>());
        Assert.Equal("high", entry.ThinkingLevel);
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect });
    }

    [Fact]
    public async Task SetThinkingLevelAlsoEmitsThinkingLevelChanged()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));
        var events = new List<AgentHarnessEvent>();
        using var _ = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        await harness.SetThinkingLevelAsync(ThinkingLevel.High);

        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect });
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelChanged });
    }

    [Fact]
    public async Task SetThinkingLevelEmitsThinkingEventBeforePersistenceCompletes()
    {
        var storage = new BlockingAppendStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl"));
        var session = new Session<JsonlSessionMetadata>(storage);
        var harness = Harness(session, FakeStream("ok"));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ThinkingLevelSelect })
            {
                observed.TrySetResult();
            }

            return Task.CompletedTask;
        });

        var setTask = harness.SetThinkingLevelAsync(ThinkingLevel.High);

        await storage.AppendStarted.WaitAsync(TimeSpan.FromSeconds(1));
        var eventWasPublishedBeforePersistenceCompleted = observed.Task.IsCompletedSuccessfully;
        storage.ReleaseAppend();
        await setTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(eventWasPublishedBeforePersistenceCompleted);
    }

    [Fact]
    public async Task SetThinkingLevelLogsHarnessIdentityAndLevels()
    {
        var session = CreateSession();
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = new RecordingLoggerFactory(provider);
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []), loggerFactory);
        var harnessId = RuntimeHelpers.GetHashCode(harness);
        using var _ = harness.Subscribe((evt, _) => Task.CompletedTask);

        await harness.SetThinkingLevelAsync(ThinkingLevel.High);

        Assert.Contains(provider.Messages, message =>
            message.Contains("Harness thinking level change requested", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("previousLevel=Off", StringComparison.Ordinal)
            && message.Contains("nextLevel=High", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Harness thinking level state updated", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("currentLevel=High", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Publishing own harness event", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("event=ThinkingLevelSelect", StringComparison.Ordinal)
            && message.Contains("listenerCount=1", StringComparison.Ordinal)
            && message.Contains("extensionHandlerCount=0", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Harness listener notification starting", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("event=ThinkingLevelSelect", StringComparison.Ordinal)
            && message.Contains("listenerCount=1", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Published own harness event", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("event=ThinkingLevelChanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetSessionNamePersistsAndEmitsSessionInfoChanged()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));
        AgentHarnessEvent? observed = null;
        using var _ = harness.Subscribe((evt, _) => { observed = evt; return Task.CompletedTask; });

        await harness.SetSessionNameAsync("named");

        Assert.Equal("named", await session.GetSessionNameAsync());
        var changed = Assert.IsType<AgentHarnessOwnEvent.SessionInfoChanged>(Assert.IsType<AgentHarnessEvent.Own>(observed).Event);
        Assert.Equal("named", changed.Name);
    }

    [Fact]
    public void QueueMutationsEmitQueueUpdateSnapshots()
    {
        var session = CreateSession();
        var harness = Harness(session, FakeStream("ok"));
        var events = new List<AgentHarnessEvent>();
        using var _ = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        harness.Steer(AgentMessages.User("steer"));
        harness.FollowUp(AgentMessages.User("follow"));
        harness.QueueNextTurn(AgentMessages.User("next"));

        Assert.Equal(3, events.OfType<AgentHarnessEvent.Own>().Count(evt => evt.Event is AgentHarnessOwnEvent.QueueUpdate));
        var latest = Assert.IsType<AgentHarnessOwnEvent.QueueUpdate>(Assert.IsType<AgentHarnessEvent.Own>(events.Last()).Event);
        Assert.Single(latest.Steer);
        Assert.Single(latest.FollowUp);
        Assert.Single(latest.NextTurn);
    }

    [Fact]
    public async Task OwnEventsDispatchToExtensionsBeforeListeners()
    {
        var session = CreateSession();
        var registry = new ExtensionRegistry();
        var seen = new List<string>();
        registry.RegisterHandler("extension:test", ExtensionEventNames.ModelSelect, (evt, _) => { seen.Add("extension:" + evt.Name); return Task.CompletedTask; });
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, [], Extensions: registry));
        using var _ = harness.Subscribe((evt, _) => { seen.Add("listener:" + ExtensionEventMapper.Name(evt)); return Task.CompletedTask; });

        await harness.SetModelAsync(new ModelDescriptor("openai", "gpt-4o", "openai-responses"), "test");

        Assert.Equal(["extension:model_select", "listener:model_select"], seen);
    }

    [Fact]
    public async Task ExtensionHandlerExceptionsDoNotPreventOwnEventListeners()
    {
        var session = CreateSession();
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.SessionInfoChanged, (_, _) => throw new InvalidOperationException("boom"));
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, [], Extensions: registry));
        AgentHarnessEvent? observed = null;
        using var _ = harness.Subscribe((evt, _) => { observed = evt; return Task.CompletedTask; });

        await harness.SetSessionNameAsync("named");

        Assert.IsType<AgentHarnessOwnEvent.SessionInfoChanged>(Assert.IsType<AgentHarnessEvent.Own>(observed).Event);
    }

    [Fact]
    public async Task PromptPropagatesThinkingLevelToStreamOptions()
    {
        var observed = ThinkingLevel.Off;
        AgentStreamAsync stream = (_, _, options, _) =>
        {
            observed = options.Reasoning is null ? ThinkingLevel.Off : Enum.Parse<ThinkingLevel>(options.Reasoning, true);
            return StreamHelper("ok");
        };
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("anthropic", "claude", "anthropic", Reasoning: true), stream, FakeCompletion, [], ThinkingLevel: ThinkingLevel.High));

        await harness.PromptAsync("hello");

        Assert.Equal(ThinkingLevel.High, observed);
    }

    [Fact]
    public async Task PromptFlushesPendingWritesWithSingleStorageBatch()
    {
        var storage = new BatchCountingStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl"));
        var session = new Session<JsonlSessionMetadata>(storage);
        AgentHarness<JsonlSessionMetadata>? harness = null;
        harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            Stream,
            FakeCompletion,
            []));

        await harness.PromptAsync("hello");

        Assert.Equal(1, storage.AppendEntriesCallCount);
        Assert.Equal([4], storage.BatchSizes);
        Assert.Collection(await session.GetEntriesAsync(),
            entry => Assert.IsType<MessageEntry>(entry),
            entry => Assert.IsType<ModelChangeEntry>(entry),
            entry => Assert.IsType<ThinkingLevelChangeEntry>(entry),
            entry => Assert.IsType<MessageEntry>(entry));

        async IAsyncEnumerable<AssistantMessageEvent> Stream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await harness!.SetModelAsync(new ModelDescriptor("openai", "gpt-4o", "openai-responses"), "test", cancellationToken);
            await harness.SetThinkingLevelAsync(ThinkingLevel.High, cancellationToken);
            var message = new AssistantMessage([new TextContent("ok")], StopReason: "stop");
            yield return new AssistantMessageEvent.Start(message);
            await Task.Yield();
            yield return new AssistantMessageEvent.Done(message);
        }
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(Session<JsonlSessionMetadata> session, AgentStreamAsync stream)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, []));

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl")));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("done"));

    private static AgentStreamAsync FakeStream(string text)
    {
        AgentStreamAsync stream = (_, _, _, _) => StreamHelper(text);
        return stream;
    }

    private sealed class BlockingAppendStorage<TMetadata>(TMetadata metadata) : ISessionStorage<TMetadata>
        where TMetadata : ISessionMetadata
    {
        private readonly MemorySessionStorage<TMetadata> _inner = new(metadata);
        private readonly TaskCompletionSource _appendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAppend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AppendStarted => _appendStarted.Task;

        public void ReleaseAppend() => _releaseAppend.TrySetResult();

        public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(cancellationToken);

        public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => _inner.GetLeafIdAsync(cancellationToken);

        public Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.SetLeafIdAsync(leafId, cancellationToken);

        public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) => _inner.CreateEntryIdAsync(cancellationToken);

        public async Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
        {
            _appendStarted.TrySetResult();
            await _releaseAppend.Task.WaitAsync(cancellationToken);
            await _inner.AppendEntryAsync(entry, cancellationToken);
        }

        public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => _inner.GetEntryAsync(id, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(Func<SessionTreeEntry, bool> predicate, CancellationToken cancellationToken = default) => _inner.FindEntriesAsync(predicate, cancellationToken);

        public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => _inner.GetLabelAsync(id, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.GetPathToRootAsync(leafId, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => _inner.GetEntriesAsync(cancellationToken);
    }

    private sealed class BatchCountingStorage<TMetadata>(TMetadata metadata) : ISessionStorage<TMetadata>
        where TMetadata : ISessionMetadata
    {
        private readonly MemorySessionStorage<TMetadata> _inner = new(metadata);
        private readonly List<int> _batchSizes = [];

        public int AppendEntriesCallCount { get; private set; }
        public IReadOnlyList<int> BatchSizes => _batchSizes.ToArray();

        public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(cancellationToken);

        public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => _inner.GetLeafIdAsync(cancellationToken);

        public Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.SetLeafIdAsync(leafId, cancellationToken);

        public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) => _inner.CreateEntryIdAsync(cancellationToken);

        public Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default) => AppendEntriesAsync([entry], cancellationToken);

        public async Task AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default)
        {
            AppendEntriesCallCount++;
            _batchSizes.Add(entries.Count);
            await _inner.AppendEntriesAsync(entries, cancellationToken);
        }

        public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => _inner.GetEntryAsync(id, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(Func<SessionTreeEntry, bool> predicate, CancellationToken cancellationToken = default) => _inner.FindEntriesAsync(predicate, cancellationToken);

        public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => _inner.GetLabelAsync(id, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.GetPathToRootAsync(leafId, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => _inner.GetEntriesAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(message);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }

    private sealed class RecordingLoggerFactory(RecordingLoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
