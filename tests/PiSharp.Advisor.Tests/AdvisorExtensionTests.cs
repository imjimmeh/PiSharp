using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Advisor.Tests;

public class AdvisorExtensionTests
{
    [Fact]
    public async Task InitializeAsync_registers_event_handlers_and_commands()
    {
        var api = new TestExtensionApi();
        var extension = new AdvisorExtension();

        await extension.InitializeAsync(api);

        Assert.Contains(api.Commands, c => c.Name == "advisor");
        Assert.Contains(api.Commands, c => c.Name == "advisor-note");

        // Both context and turn_end should be subscribed; raise each without error.
        api.SettingsImpl.SetAsync("enabled", true);
        api.SettingsImpl.SetAsync("model", "fp/test");
        await api.RaiseAsync(ExtensionEventNames.Context,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Context([AgentMessages.User("hello")])),
            new AgentHarnessOwnEvent.Context([AgentMessages.User("hello")]));
        await api.RaiseAsync(ExtensionEventNames.TurnEnd,
            new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(AgentMessages.Assistant("hi"), [])),
            new AgentEvent.TurnEnd(AgentMessages.Assistant("hi"), []));
    }

    [Fact]
    public async Task TurnEnd_drives_review_emits_event_and_persists_entry()
    {
        var api = new TestExtensionApi();
        api.SettingsImpl.SetAsync("enabled", true);
        api.SettingsImpl.SetAsync("model", "fp/test");
        api.CompletionImpl.CompleteHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Ok, "there is a risk here", null, null));
        var extension = new AdvisorExtension();
        await extension.InitializeAsync(api);

        await api.RaiseAsync(ExtensionEventNames.TurnEnd,
            new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(AgentMessages.Assistant("done"), [])),
            new AgentEvent.TurnEnd(AgentMessages.Assistant("done"), []));

        WaitUntil(() => api.EventsImpl.Emitted.Count > 0);
        var (name, payload) = Assert.Single(api.EventsImpl.Emitted);
        Assert.Equal(ExtensionEventNames.AdvisorNote, name);
        var evt = Assert.IsType<ExtensionAdvisorEvent>(payload);
        Assert.Equal("concern", evt.Note.Kind);

        Assert.Contains(api.SessionImpl.Appended, a => a.Type == "advisor_note");
    }

    [Fact]
    public async Task AdvisorCommand_on_enables_feature_via_settings()
    {
        var api = new TestExtensionApi();
        api.SettingsImpl.SetAsync("model", "fp/test");
        var extension = new AdvisorExtension();
        await extension.InitializeAsync(api);

        Assert.False(extension.Enabled);
        var command = Assert.Single(api.Commands.Where(c => c.Name == "advisor"));
        await InvokeAsync(command, "on", api);

        Assert.True(extension.Enabled);
        Assert.Contains(api.SentMessages, m => TextContains(m, "Advisor enabled."));
    }

    [Fact]
    public async Task AdvisorCommand_lists_recent_notes()
    {
        var api = new TestExtensionApi();
        api.SettingsImpl.SetAsync("enabled", true);
        api.SettingsImpl.SetAsync("model", "fp/test");
        api.CompletionImpl.CompleteHandler = (_, _, _, _, _, _) =>
            Task.FromResult(new ExtensionCompletionResult(ExtensionCompletionStatus.Ok, "a note", null, null));
        var extension = new AdvisorExtension();
        await extension.InitializeAsync(api);

        await api.RaiseAsync(ExtensionEventNames.TurnEnd,
            new AgentHarnessEvent.Core(new AgentEvent.TurnEnd(AgentMessages.Assistant("done"), [])),
            new AgentEvent.TurnEnd(AgentMessages.Assistant("done"), []));
        WaitUntil(() => api.EventsImpl.Emitted.Count > 0);

        var before = api.SentMessages.Count;
        var command = Assert.Single(api.Commands.Where(c => c.Name == "advisor"));
        await InvokeAsync(command, "", api);

        Assert.True(api.SentMessages.Count > before);
        Assert.Contains(api.SentMessages.Skip(before), m => TextContains(m, "a note"));
    }

    private static Task InvokeAsync(ExtensionCommandRegistration command, string args, TestExtensionApi api)
        => command.InvokeAsync(new ExtensionCommandContext(
            command.Name,
            args,
            NoExtensionUi.Instance,
            api.Session,
            null!,
            null!,
            new Dictionary<string, object?>(),
            CancellationToken.None));

    private static bool TextContains(AgentMessage message, string text)
        => message is UserMessage u && string.Concat(u.Content.OfType<TextContent>().Select(t => t.Text)).Contains(text, StringComparison.Ordinal);

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
