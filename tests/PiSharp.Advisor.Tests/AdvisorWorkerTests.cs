using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Advisor.Tests;

public class AdvisorWorkerTests
{
    private readonly List<ExtensionAdvisorEvent> _emitted = [];
    private readonly List<ExtensionAdvisorNote> _notes = [];

    private AdvisorWorker CreateWorker(TestCompletionApi completion, AdvisorTranscript? transcript = null)
    {
        _emitted.Clear();
        _notes.Clear();
        var worker = new AdvisorWorker(completion, "session-1", transcript ?? new AdvisorTranscript(12), evt =>
        {
            _emitted.Add(evt);
            _notes.Add(evt.Note);
        });
        return worker;
    }

    [Fact]
    public void Configure_splits_provider_and_model()
    {
        var completion = new TestCompletionApi();
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");
        Assert.Equal("fp", worker.Provider);
        Assert.Equal("test", worker.ModelId);
        Assert.True(worker.Enabled);
    }

    [Fact]
    public void OnTurnEnd_emits_classified_note_when_disabled_model_configured()
    {
        var completion = new TestCompletionApi();
        completion.CompleteHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Ok, "This is a blocker: must not merge.", null, null));
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");

        worker.OnTurnEnd("turn-1");
        WaitUntil(() => _emitted.Count > 0);

        var evt = Assert.Single(_emitted);
        Assert.Equal("session-1", evt.SessionId);
        Assert.Equal("turn-1", evt.TurnId);
        Assert.Equal("blocker", evt.Note.Kind);
        Assert.Equal("test", evt.Note.Model);
    }

    [Fact]
    public void OnTurnEnd_passes_transcript_to_completion()
    {
        var completion = new TestCompletionApi();
        var transcript = new AdvisorTranscript(12);
        transcript.Append([AgentMessages.User("hello"), AgentMessages.Assistant("hi")]);
        var worker = CreateWorker(completion, transcript);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");

        worker.OnTurnEnd("turn-1");
        WaitUntil(() => completion.Calls.Count > 0);

        var call = Assert.Single(completion.Calls);
        Assert.Equal("test", call.ModelId);
        Assert.True(call.MessageCount > 0);
    }

    [Fact]
    public void Timeout_result_emits_timeout_note()
    {
        var completion = new TestCompletionApi();
        completion.CompleteHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Timeout, null, null, null));
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");

        worker.OnTurnEnd("turn-1");
        WaitUntil(() => _emitted.Count > 0);

        Assert.Equal("timeout", Assert.Single(_emitted).Note.Kind);
    }

    [Fact]
    public void Error_result_emits_error_note_and_does_not_throw()
    {
        var completion = new TestCompletionApi();
        completion.CompleteHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Error, null, "boom", null));
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");

        worker.OnTurnEnd("turn-1");
        WaitUntil(() => _emitted.Count > 0);

        Assert.Equal("error", Assert.Single(_emitted).Note.Kind);
    }

    [Fact]
    public void Exception_from_completion_emits_error_note()
    {
        var completion = new TestCompletionApi();
        completion.CompleteHandler = (_, _, _, _, _, _) => throw new InvalidOperationException("provider down");
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");

        worker.OnTurnEnd("turn-1");
        WaitUntil(() => _emitted.Count > 0);

        Assert.Equal("error", Assert.Single(_emitted).Note.Kind);
    }

    [Fact]
    public void Enabled_without_model_emits_error_note_without_calling_completion()
    {
        var completion = new TestCompletionApi();
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, null, 10000, 512, 12, true), model: null);

        worker.OnTurnEnd("turn-1");

        var note = Assert.Single(_notes);
        Assert.Equal("error", note.Kind);
        Assert.Empty(completion.Calls);
    }

    [Fact]
    public void Disabled_worker_is_a_no_op()
    {
        var completion = new TestCompletionApi();
        var worker = CreateWorker(completion);
        worker.Configure(AdvisorOptions.Default with { Model = "fp/test" }, "fp/test");

        worker.OnTurnEnd("turn-1");

        Assert.Empty(_notes);
        Assert.Empty(completion.Calls);
    }

    [Fact]
    public async Task Concurrent_turns_are_coalesced_to_one_rerun()
    {
        var completion = new TestCompletionApi();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        completion.CompleteHandler = async (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            if (calls == 1) firstStarted.SetResult();
            await release.Task;
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Ok, "note", null, null);
        };
        var worker = CreateWorker(completion);
        worker.Configure(new AdvisorOptions(true, "fp/test", 10000, 512, 12, true), "fp/test");

        worker.OnTurnEnd("turn-1"); // starts review 1 (blocked)
        await firstStarted.Task;
        worker.OnTurnEnd("turn-2"); // coalesced
        worker.OnTurnEnd("turn-3"); // coalesced into the single pending slot

        release.SetResult();

        WaitUntil(() => calls >= 2);
        Assert.Equal(2, calls); // only one re-review despite three turn_ends
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within timeout.");
            Thread.Sleep(10);
        }
    }
}
