using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Serialization;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;
using PiSharp.Runtime.Subagents;
using Xunit;

namespace PiSharp.Runtime.Tests.Subagents;

public sealed class SubagentSessionServiceTests : IAsyncLifetime
{
    private string _tempRoot = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pisharp-subagent-tests-" + Guid.NewGuid().ToString("N"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { }
        }
        return Task.CompletedTask;
    }

    private string TempDir() => Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsyncCreatesChildHandleWithoutReplacingParentSession()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var originalSessionId = runtime.Session.Metadata.Id;
        var service = new SubagentSessionService(runtime);

        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);

        Assert.NotNull(handle);
        Assert.NotEqual(originalSessionId, handle.Session.Metadata.Id);
        Assert.Equal(originalSessionId, runtime.Session.Metadata.Id);
        Assert.NotNull(handle.Harness);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncPropagatesSessionNameToChildSession()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var options = new SubagentSessionOptions(SessionName: "child-session");

        var handle = await service.CreateAsync(options, CancellationToken.None);

        var sessionName = await handle.Session.GetSessionNameAsync(CancellationToken.None);
        Assert.Equal("child-session", sessionName);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncPropagatesParentSessionPathToChildSessionMetadata()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var options = new SubagentSessionOptions(ParentSessionPath: initial.Metadata.Path);

        var handle = await service.CreateAsync(options, CancellationToken.None);

        Assert.Equal(initial.Metadata.Path, handle.Session.Metadata.ParentSessionPath);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task DisposeAsyncRemovesHandleAndPreventsReDispose()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var childId = handle.SessionId;

        await service.DisposeAsync(childId, CancellationToken.None);

        var ex = await Record.ExceptionAsync(() => service.DisposeAsync(childId, CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task CreateAsyncCreatesMultipleIsolatedChildSessions()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var originalSessionId = runtime.Session.Metadata.Id;
        var service = new SubagentSessionService(runtime);

        var handle1 = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var handle2 = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);

        Assert.NotEqual(handle1.SessionId, handle2.SessionId);
        Assert.NotEqual(handle1.Session.Metadata.Id, handle2.Session.Metadata.Id);
        Assert.Equal(originalSessionId, runtime.Session.Metadata.Id);

        await service.DisposeAsync(handle1.SessionId, CancellationToken.None);
        await service.DisposeAsync(handle2.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncThrowsArgumentNullExceptionForNullOptions()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.CreateAsync(null!, CancellationToken.None));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public async Task DisposeAsyncThrowsOperationCanceledExceptionWhenTokenCancelled()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DisposeAsync(handle.SessionId, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task HandleDisposeAsyncIsIdempotent()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);

        var first = await Record.ExceptionAsync(() => handle.DisposeAsync().AsTask());
        Assert.Null(first);

        var second = await Record.ExceptionAsync(() => handle.DisposeAsync().AsTask());
        Assert.Null(second);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsyncCleansUpChildSessionOnPartialFailure()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root, PersistImmediately: true);
        var initial = await repo.CreateAsync(createOptions);
        var callCount = 0;
        AgentHarness<JsonlSessionMetadata> ThrowingHarness(ISession<JsonlSessionMetadata> session)
        {
            if (Interlocked.Increment(ref callCount) > 1)
                throw new InvalidOperationException("harness creation failed");
            return Harness(session);
        }
        var runtime = new SessionRuntime(repo, createOptions, ThrowingHarness, initial);
        var service = new SubagentSessionService(runtime);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None));
        Assert.Equal(2, callCount);
        Assert.Equal("harness creation failed", ex.Message);

        var sessions = await repo.ListAsync(new JsonlSessionListOptions(root));
        Assert.Single(sessions);
        Assert.Equal(initial.Metadata.Id, sessions[0].Id);
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    [Fact]
    public async Task PromptAsyncRunsChildHarnessThroughToolTurnAndFinalAnswer()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var echoTool = new EchoTool();

        var runtime = new SessionRuntime(repo, createOptions,
            session => new AgentHarness<JsonlSessionMetadata>(
                new AgentHarnessOptions<JsonlSessionMetadata>(
                    session,
                    new ModelDescriptor("test", "test", "test"),
                    CreateMultiTurnStream("echo", "{\"text\":\"hi\"}", "final summary"),
                    FakeCompletion,
                    [echoTool])),
            initial);
        var originalSessionId = runtime.Session.Metadata.Id;
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);

        var result = await service.PromptAsync(handle.SessionId, "run echo", CancellationToken.None);

        Assert.Equal(handle.SessionId, result.SessionId);
        Assert.Contains(result.FinalMessage.Content.OfType<TextContent>(), tc => tc.Text.Contains("final summary"));
        Assert.Equal("stop", result.FinalMessage.StopReason);
        Assert.Equal(originalSessionId, runtime.Session.Metadata.Id);
        Assert.NotNull(result.Messages);
        Assert.Contains(result.Messages, m => m is ToolResultMessage);
        Assert.Contains(result.Messages.OfType<UserMessage>(), m => m.Content.OfType<TextContent>().Any(tc => tc.Text == "run echo"));

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task PromptAsyncThrowsForUnknownSessionId()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PromptAsync("nonexistent", "hello", CancellationToken.None));

        Assert.Contains("nonexistent", ex.Message, StringComparison.Ordinal);
    }

    private static AgentStreamAsync CreateMultiTurnStream(string toolName, string argsJson, string finalText)
        => (_, context, _, ct) => MultiTurnStreamImpl(toolName, argsJson, finalText, context, ct);

    private static async IAsyncEnumerable<AssistantMessageEvent> MultiTurnStreamImpl(
        string toolName, string argsJson, string finalText, AgentContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        AssistantMessage msg;
        if (context.Messages.OfType<ToolResultMessage>().Any())
        {
            msg = new AssistantMessage([new TextContent(finalText)], StopReason: "stop");
        }
        else
        {
            using var args = JsonDocument.Parse(argsJson);
            msg = new AssistantMessage(
                [new ToolCallContent("call-1", toolName, args.RootElement.Clone())],
                StopReason: "tool_use");
        }
        yield return new AssistantMessageEvent.Start(msg);
        yield return new AssistantMessageEvent.Done(msg);
    }

    [Fact]
    public async Task PromptAsyncPublishesJsPiLifecycleEventsForChildSession()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var events = new List<object>();
        service.Subscribe(handle.SessionId, evt => { events.Add(evt); return Task.CompletedTask; });

        await service.PromptAsync(handle.SessionId, "task", CancellationToken.None);

        var json = string.Join("\n", events.Select(AgentJsonSerializer.Serialize));
        Assert.Contains("\"type\":\"agent_start\"", json);
        Assert.Contains("\"type\":\"turn_start\"", json);
        Assert.Contains("\"type\":\"message_end\"", json);
        Assert.Contains("\"type\":\"turn_end\"", json);
        Assert.Contains("\"type\":\"agent_end\"", json);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task SubscribeDisposeStopsEventDelivery()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var events = new List<object>();
        var subscription = service.Subscribe(handle.SessionId, evt => { events.Add(evt); return Task.CompletedTask; });

        await service.PromptAsync(handle.SessionId, "first", CancellationToken.None);
        Assert.NotEmpty(events);

        subscription.Dispose();
        var countAfterDispose = events.Count;

        await service.PromptAsync(handle.SessionId, "second", CancellationToken.None);
        Assert.Equal(countAfterDispose, events.Count);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task SubscriberCallbackFailureIsolatesSiblingCallbacks()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var healthyEvents = new List<object>();

        service.Subscribe(handle.SessionId, _ => throw new InvalidOperationException("simulated failure"));
        service.Subscribe(handle.SessionId, evt => { healthyEvents.Add(evt); return Task.CompletedTask; });

        await service.PromptAsync(handle.SessionId, "task", CancellationToken.None);

        Assert.NotEmpty(healthyEvents);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task DisposeAllAsyncClearsHandlesAndIsIdempotent()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var first = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var second = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        service.Subscribe(first.SessionId, _ => Task.CompletedTask);
        service.Subscribe(second.SessionId, _ => Task.CompletedTask);

        await service.DisposeAllAsync(CancellationToken.None);
        await service.DisposeAllAsync(CancellationToken.None);

        Assert.Null(service.GetHandle(first.SessionId));
        Assert.Null(service.GetHandle(second.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptAsync(first.SessionId, "after cleanup", CancellationToken.None));
    }

    [Fact]
    public async Task SteerAsyncQueuesSteeringMessageForChildOnly()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var parentQueueUpdates = new List<AgentHarnessOwnEvent.QueueUpdate>();
        var childQueueUpdates = new List<AgentHarnessOwnEvent.QueueUpdate>();
        runtime.Harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.QueueUpdate update }) parentQueueUpdates.Add(update);
            return Task.CompletedTask;
        });
        handle.Harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.QueueUpdate update }) childQueueUpdates.Add(update);
            return Task.CompletedTask;
        });

        await service.SteerAsync(handle.SessionId, "steer child", CancellationToken.None);

        Assert.Empty(parentQueueUpdates);
        var update = Assert.Single(childQueueUpdates);
        var message = Assert.Single(update.Steer);
        Assert.Equal("steer child", TextOf(message));

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task FollowUpAsyncQueuesFollowUpForChildOnly()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var parentQueueUpdates = new List<AgentHarnessOwnEvent.QueueUpdate>();
        var childQueueUpdates = new List<AgentHarnessOwnEvent.QueueUpdate>();
        runtime.Harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.QueueUpdate update }) parentQueueUpdates.Add(update);
            return Task.CompletedTask;
        });
        handle.Harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.QueueUpdate update }) childQueueUpdates.Add(update);
            return Task.CompletedTask;
        });

        await service.FollowUpAsync(handle.SessionId, "follow child", CancellationToken.None);

        Assert.Empty(parentQueueUpdates);
        var update = Assert.Single(childQueueUpdates);
        var message = Assert.Single(update.FollowUp);
        Assert.Equal("follow child", TextOf(message));

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task AbortAsyncCancelsChildWithoutAbortingParent()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new SessionRuntime(repo, createOptions,
            session => session.Metadata.Id == initial.Metadata.Id
                ? Harness(session)
                : new AgentHarness<JsonlSessionMetadata>(
                    new AgentHarnessOptions<JsonlSessionMetadata>(
                        session,
                        new ModelDescriptor("test", "test", "test"),
                        BlockingStream(childStarted),
                        FakeCompletion,
                        [])),
            initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var childPrompt = service.PromptAsync(handle.SessionId, "wait", CancellationToken.None);
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.AbortAsync(handle.SessionId, CancellationToken.None);

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await childPrompt);
        var parentResult = await runtime.Harness.PromptAsync("parent still works", CancellationToken.None);
        Assert.Equal("ok", TextOf(parentResult));

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task DisposeAsyncRemovesChildHandleAndSubscriptions()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var events = new List<object>();
        service.Subscribe(handle.SessionId, evt => { events.Add(evt); return Task.CompletedTask; });

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);

        Assert.Null(service.GetHandle(handle.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptAsync(handle.SessionId, "after dispose", CancellationToken.None));
    }

    [Fact]
    public async Task SetModelAsyncAndSetThinkingLevelAsyncUpdateChildOnly()
    {
        var root = TempDir();
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), CancellationToken.None);
        var childModel = new ModelDescriptor("child-provider", "child-model", "child label");

        await service.SetModelAsync(handle.SessionId, childModel, CancellationToken.None);
        await service.SetThinkingLevelAsync(handle.SessionId, ThinkingLevel.High, CancellationToken.None);

        Assert.Equal(childModel, handle.Harness.Model);
        Assert.Equal(new ModelDescriptor("test", "test", "test"), runtime.Harness.Model);
        Assert.Equal(ThinkingLevel.High, handle.Harness.ThinkingLevel);
        Assert.NotEqual(ThinkingLevel.High, runtime.Harness.ThinkingLevel);

        await service.DisposeAsync(handle.SessionId, CancellationToken.None);
    }

    private sealed class EchoTool : IAgentTool
    {
        public string Name => "echo";
        public string Label => "echo";
        public string Description => "Echoes back the input text";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"text\":{\"type\":\"string\"}}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;

        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(
            string toolCallId,
            JsonElement parameters,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback<object?>? onUpdate = null)
        {
            var text = parameters.TryGetProperty("text", out var textProp)
                ? textProp.GetString() ?? "no text"
                : "no text";
            return Task.FromResult(new AgentToolResult<object?>([new TextContent(text)], new { }));
        }
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static AgentStreamAsync BlockingStream(TaskCompletionSource started)
        => (model, context, options, cancellationToken) => BlockingStreamImpl(started, cancellationToken);

    private static async IAsyncEnumerable<AssistantMessageEvent> BlockingStreamImpl(
        TaskCompletionSource started,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static string? TextOf(AgentMessage message)
        => message switch
        {
            UserMessage user => user.Content.OfType<TextContent>().SingleOrDefault()?.Text,
            AssistantMessage assistant => assistant.Content.OfType<TextContent>().SingleOrDefault()?.Text,
            ToolResultMessage toolResult => toolResult.Content.OfType<TextContent>().SingleOrDefault()?.Text,
            _ => null
        };
}
