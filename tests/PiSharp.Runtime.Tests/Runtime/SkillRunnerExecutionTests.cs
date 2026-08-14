using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Serialization;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

/// <summary>
/// P04 (GAP-56): the per-skill runner hook — a skill with a
/// <see cref="ExtensionSkillRunner"/> executes through the harness when
/// invoked via <c>/skill:&lt;name&gt;</c>, emitting
/// <c>skill_execution_start</c>/<c>skill_execution_end</c>, and falls back to
/// markdown injection when the runner fails or is absent.
/// </summary>
public sealed class SkillRunnerExecutionTests
{
    [Fact]
    public async Task SkillWithRunnerExecutesAndEmitsSkillExecutionEvents()
    {
        var (harness, observedUserTexts) = CreateHarness();
        var runnerInvocations = new List<ExtensionSkillRunContext>();
        harness.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "runnable", "Runnable", "skill-body", "/repo/runnable/SKILL.md",
            Runner: (context, _) =>
            {
                runnerInvocations.Add(context);
                return Task.FromResult(new ExtensionSkillRunResult("runner-output"));
            }));
        var events = CaptureExecutionEvents(harness);

        await harness.PromptAsync("/skill:runnable arg1 arg2");

        var context = Assert.Single(runnerInvocations);
        Assert.Equal("runnable", context.Name);
        Assert.Equal("skill-body", context.Body);
        Assert.Equal("arg1 arg2", context.AdditionalInstructions);
        Assert.Equal(["arg1", "arg2"], context.Args);
        Assert.Equal(["start", "end"], events);
        Assert.Contains(observedUserTexts, text => text.Contains("runner-output", StringComparison.Ordinal));
        Assert.DoesNotContain(observedUserTexts, text => text.Contains("skill-body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkillWithoutRunnerInjectsMarkdownAndEmitsNoExecutionEvents()
    {
        var (harness, observedUserTexts) = CreateHarness();
        harness.RegisterSkill("extension:test", new ExtensionSkillRegistration("plain", "Plain", "markdown-body", "/repo/plain/SKILL.md"));
        var events = CaptureExecutionEvents(harness);

        await harness.PromptAsync("/skill:plain");

        Assert.Empty(events);
        Assert.Contains(observedUserTexts, text => text.Contains("markdown-body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkillRunnerFailureFallsBackToMarkdownAndEmitsErrorExecutionEnd()
    {
        var (harness, observedUserTexts) = CreateHarness();
        harness.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "failing", "Failing", "fallback-body", "/repo/failing/SKILL.md",
            Runner: (_, _) => throw new InvalidOperationException("boom")));
        var events = CaptureExecutionEvents(harness);

        await harness.PromptAsync("/skill:failing");

        Assert.Equal(["start", "end"], events);
        Assert.Contains(observedUserTexts, text => text.Contains("fallback-body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunnerCanReturnNullContentToKeepMarkdownInjection()
    {
        var (harness, observedUserTexts) = CreateHarness();
        harness.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "delegating", "Delegating", "delegate-body", "/repo/delegating/SKILL.md",
            Runner: (_, _) => Task.FromResult(new ExtensionSkillRunResult(null))));

        await harness.PromptAsync("/skill:delegating");

        Assert.Contains(observedUserTexts, text => text.Contains("delegate-body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunnerReceivesSkillContentAndArgsInContext()
    {
        var (harness, _) = CreateHarness();
        ExtensionSkillRunContext? captured = null;
        harness.RegisterSkill("extension:test", new ExtensionSkillDefinition(
            "capturing", "Capturing", "capture-body", "/repo/capturing/SKILL.md",
            Runner: (context, _) =>
            {
                captured = context;
                return Task.FromResult(new ExtensionSkillRunResult("ok"));
            }));

        await harness.PromptAsync("/skill:capturing --flag value");

        Assert.NotNull(captured);
        Assert.Equal("capture-body", captured!.Body);
        Assert.Equal(["--flag", "value"], captured.Args);
    }

    private static (AgentHarness<JsonlSessionMetadata> Harness, List<string> ObservedUserTexts) CreateHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-skill-runner-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = repo.CreateAsync(createOptions).GetAwaiter().GetResult();
        var observedUserTexts = new List<string>();
        AgentStreamAsync stream = (model, context, options, token) =>
        {
            observedUserTexts.Add(context.Messages.OfType<UserMessage>().Last().Content.OfType<TextContent>().Single().Text);
            return FakeStream(model, context, options, token);
        };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, []));
        return (harness, observedUserTexts);
    }

    private static List<string> CaptureExecutionEvents(AgentHarness<JsonlSessionMetadata> harness)
    {
        var events = new List<string>();
        harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SkillExecutionStart }) events.Add("start");
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SkillExecutionEnd }) events.Add("end");
            return Task.CompletedTask;
        });
        return events;
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(
        ModelDescriptor _,
        AgentContext __,
        AgentStreamOptions ___,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
