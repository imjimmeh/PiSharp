using System.Reflection;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Agent.Tests.Harness;

public sealed class AgentHarnessConcurrencyTests
{
    private const int ProducerCount = 8;
    private const int MessagesPerProducer = 500;

    private static readonly Type HarnessType = typeof(AgentHarness<JsonlSessionMetadata>);
    private static readonly MethodInfo DrainSteeringQueue = HarnessType.GetMethod("DrainSteeringQueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DrainFollowUpQueue = HarnessType.GetMethod("DrainFollowUpQueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DrainQueue = HarnessType.GetMethod("DrainQueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo FlushWrites = HarnessType.GetMethod("FlushWritesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo NextTurnQueue = HarnessType.GetField("_nextTurnQueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo PendingWrites = HarnessType.GetField("_pendingWrites", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task ConcurrentSteeringFollowUpAndNextTurnMessagesAreDrainedExactlyOnce()
    {
        var harness = Harness(CreateSession(), FakeStream("ok"));
        var drained = new List<AgentMessage>();

        var producers = Enumerable.Range(0, ProducerCount)
            .Select(producer => Task.Run(() =>
            {
                for (var i = 0; i < MessagesPerProducer; i++)
                {
                    harness.Steer(AgentMessages.User($"steer-{producer}-{i}"));
                    harness.FollowUp(AgentMessages.User($"follow-{producer}-{i}"));
                    harness.QueueNextTurn(AgentMessages.User($"next-{producer}-{i}"));
                }
            }))
            .ToArray();

        // Drain every queue continuously while producers inject, then sweep once
        // more after all producers have finished so nothing is left behind.
        while (producers.Any(producer => !producer.IsCompleted))
        {
            await DrainAllAsync();
        }

        await DrainAllAsync();
        await Task.WhenAll(producers);
        Assert.All(producers, producer => Assert.True(producer.IsCompletedSuccessfully));

        var counts = new Dictionary<AgentMessage, int>(ReferenceEqualityComparer.Instance);
        foreach (var message in drained) counts[message] = counts.GetValueOrDefault(message) + 1;

        var expectedPerQueue = ProducerCount * MessagesPerProducer;
        Assert.Equal(3 * expectedPerQueue, drained.Count);
        Assert.Equal(3 * expectedPerQueue, counts.Count);
        Assert.All(counts, pair => Assert.Equal(1, pair.Value));

        async Task DrainAllAsync()
        {
            drained.AddRange(await InvokeDrainAsync(DrainSteeringQueue, [CancellationToken.None]));
            drained.AddRange(await InvokeDrainAsync(DrainFollowUpQueue, [CancellationToken.None]));
            drained.AddRange(await InvokeDrainAsync(DrainQueue, [(List<AgentMessage>)NextTurnQueue.GetValue(harness)!]));
        }

        async Task<IReadOnlyList<AgentMessage>> InvokeDrainAsync(MethodInfo method, object?[] args)
        {
            var task = (Task<IReadOnlyList<AgentMessage>>)method.Invoke(harness, args)!;
            return await task;
        }
    }

    [Fact]
    public async Task ConcurrentWritesAndFlushesDuringActiveTurnLoseNoWrites()
    {
        const int flusherProducers = 8;
        const int writesPerProducer = 100;
        var session = CreateSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = Harness(session, GatedStream(entered, release));

        var turn = harness.PromptAsync("turn");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While the turn is active, every model change is queued as a pending write
        // instead of being appended directly; concurrent flushes race the queue.
        var producers = Enumerable.Range(0, flusherProducers)
            .Select(producer => Task.Run(async () =>
            {
                for (var i = 0; i < writesPerProducer; i++)
                {
                    await harness.SetModelAsync(new ModelDescriptor("provider", $"model-{producer}-{i}", "api"));
                }
            }))
            .ToArray();

        var flusher = Task.Run(async () =>
        {
            while (!producers.All(producer => producer.IsCompleted))
            {
                var flush = (Task)FlushWrites.Invoke(harness, [CancellationToken.None])!;
                await flush;
            }
        });

        await Task.WhenAll(producers);
        await flusher;
        release.SetResult();
        await turn;

        Assert.All(producers.Append(flusher), task => Assert.True(task.IsCompletedSuccessfully));

        var entries = await session.GetEntriesAsync();
        Assert.Equal(flusherProducers * writesPerProducer, entries.OfType<ModelChangeEntry>().Count());
        Assert.Empty((System.Collections.IList)PendingWrites.GetValue(harness)!);
    }

    [Fact]
    public async Task ConcurrentSubscribeAndUnsubscribeDuringEventDispatchDoesNotCorruptListeners()
    {
        var harness = Harness(CreateSession(), FakeStream("ok"));
        using var _ = harness.Subscribe((_, _) => Task.CompletedTask);

        var publishers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    await harness.PublishOwnEventAsync(new AgentHarnessOwnEvent.CustomEvent("concurrency_probe", new { i }), CancellationToken.None);
                }
            }))
            .ToArray();

        var churners = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    using var subscription = harness.Subscribe((_, _) => Task.CompletedTask);
                }
            }))
            .ToArray();

        await Task.WhenAll(publishers.Concat(churners));
        Assert.All(publishers.Concat(churners), task => Assert.True(task.IsCompletedSuccessfully));
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(Session<JsonlSessionMetadata> session, AgentStreamAsync stream)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, []));

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl")));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("summarized"));

    private static AgentStreamAsync FakeStream(string text)
    {
        AgentStreamAsync stream = (_, _, _, _) => StreamHelper(text);
        return stream;
    }

    private static AgentStreamAsync GatedStream(TaskCompletionSource entered, TaskCompletionSource release)
    {
        AgentStreamAsync stream = (_, _, _, _) => GatedStreamHelper(entered, release);
        return stream;
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(message);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> GatedStreamHelper(TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.TrySetResult();
        await release.Task;
        var message = new AssistantMessage([new TextContent("ok")], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(message);
    }
}
