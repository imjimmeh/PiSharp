using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Extensions;
using PiSharp.Tests.AgentMessaging;
using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class AgentMessagingExtensionTests : IAsyncLifetime
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pisharp-agentmessaging-ext", Guid.NewGuid().ToString("N"));
    private TestExtensionApi _api = null!;
    private AgentMessagingExtension _extension = null!;

    public Task InitializeAsync()
    {
        _api = new TestExtensionApi { Cwd = Path.GetTempPath() };
        _api.SettingsImpl.SetAsync("agentMessaging.storeDirectory", _storeDir).GetAwaiter().GetResult();
        _extension = new AgentMessagingExtension();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _extension.DisposeAsync();
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    [Fact]
    public async Task Initialize_RegistersToolsSkillAndSession()
    {
        await _extension.InitializeAsync(_api);

        var toolNames = _api.RegisteredTools.Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["agent_message", "hub"], toolNames);
        Assert.Contains(_api.RegisteredSkills, s => s.Name == AgentMessagingExtension.SkillName);
        Assert.Equal("root-session", _extension.AgentId);
        Assert.NotNull(_extension.Roster);
        Assert.NotNull(_extension.Router);
        Assert.True(_extension.Roster!.TryGet("root-session", out var self));
        Assert.Equal("main", self.Role);
        Assert.Equal(AgentStatus.Running, self.Status);
    }

    [Fact]
    public async Task Initialize_Disabled_RegistersNothing()
    {
        await _api.SettingsImpl.SetAsync("agentMessaging.enabled", false);

        await _extension.InitializeAsync(_api);

        Assert.Empty(_api.RegisteredTools);
        Assert.Empty(_api.RegisteredSkills);
        Assert.Null(_extension.Roster);
        Assert.Null(_extension.Router);
    }

    [Fact]
    public async Task BeforePromptRender_InjectsMessagingBrief()
    {
        await _extension.InitializeAsync(_api);
        // A child joins the family so the brief has content.
        _extension.Roster!.Register(TestAgents.Agent("child-1", parent: "root-session"));

        var evt = await _api.RaiseAsync(ExtensionEventNames.BeforePromptRender);

        Assert.NotNull(evt.ModifiedPromptDocumentPatch);
        var section = Assert.Single(evt.ModifiedPromptDocumentPatch!.AppendSections!);
        Assert.Equal(AgentMessagingBriefFormatter.BriefSectionId, section.Id);
        Assert.Equal("instructions", section.Slot);
        Assert.Equal(PromptDocumentContentTypes.Markdown, section.ContentType);
        Assert.Contains("## Messaging Brief", section.Content);
        Assert.Contains("child-1", section.Content);
    }

    [Fact]
    public async Task BeforePromptRender_WithBriefDisabled_DoesNotPatch()
    {
        await _api.SettingsImpl.SetAsync("agentMessaging.briefInPrompt", false);
        await _extension.InitializeAsync(_api);

        var evt = await _api.RaiseAsync(ExtensionEventNames.BeforePromptRender);

        Assert.Null(evt.ModifiedPromptDocumentPatch);
    }

    [Fact]
    public async Task MessageToLocalAgent_SteersHarnessAndEmitsWireEvent()
    {
        await _extension.InitializeAsync(_api);
        _extension.Roster!.Register(TestAgents.Agent("child-1", parent: "root-session"));
        var router = _extension.Router!;

        // Child messages its parent (the local session).
        var result = await router.SendAsync("child-1", ["parent"], "parent, respond", AgentMessageDelivery.Steer);

        Assert.False(result.IsError);
        Assert.Equal(AgentMessageStatus.Delivered, Assert.Single(result.Recipients).Status);

        // Injected into the local harness as a Steer user message.
        var injected = Assert.Single(_api.SentMessages);
        Assert.Equal(ExtensionMessageDelivery.Steer, injected.Delivery);
        var user = Assert.IsType<UserMessage>(injected.Message);
        Assert.Contains("parent, respond", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);

        // Published on the daemon wire as agent_message.
        Assert.Contains(_api.EmittedClientEvents, e => e.EventName == AgentMessagingEventNames.AgentMessage);
    }

    [Fact]
    public async Task MessageToOtherAgent_DoesNotInjectIntoLocalHarness()
    {
        await _extension.InitializeAsync(_api);
        _extension.Roster!.Register(TestAgents.Agent("child-1", parent: "root-session"));
        _extension.Roster!.Register(TestAgents.Agent("child-2", parent: "root-session"));

        var result = await _extension.Router!.SendAsync("child-1", ["child-2"], "sibling note", AgentMessageDelivery.Steer);

        Assert.False(result.IsError);
        Assert.Empty(_api.SentMessages); // not addressed to this session
        Assert.Contains(_api.EmittedClientEvents, e => e.EventName == AgentMessagingEventNames.AgentMessage);
    }

    [Fact]
    public async Task RosterChange_EmitsRosterUpdateEvent()
    {
        await _extension.InitializeAsync(_api);

        _extension.Roster!.Register(TestAgents.Agent("child-1", parent: "root-session"));

        await WaitUntilAsync(() => _api.EmittedClientEvents.Any(e => e.EventName == AgentMessagingEventNames.AgentRosterUpdate));
        var emitted = _api.EmittedClientEvents.First(e => e.EventName == AgentMessagingEventNames.AgentRosterUpdate);
        var roster = Assert.IsType<AgentRoster>(emitted.Payload);
        Assert.Contains(roster.Agents, a => a.AgentId == "child-1");
    }

    [Fact]
    public async Task SubagentCreatedEvent_RegistersChildInRoster()
    {
        await _extension.InitializeAsync(_api);

        var payload = JsonSerializer.SerializeToElement(new { id = "sub-1", type = "scout" });
        await _api.RaiseAsync("subagents:created", payload);

        Assert.True(_extension.Roster!.TryGet("sub-1", out var child));
        Assert.Equal("subagent", child.Role);
        Assert.Equal("root-session", child.ParentAgentId);
    }

    [Fact]
    public async Task SubagentCompletedEvent_MarksChildGone()
    {
        await _extension.InitializeAsync(_api);
        _extension.Roster!.Register(TestAgents.Agent("sub-1", parent: "root-session"));

        await _api.RaiseAsync("subagents:completed", JsonSerializer.SerializeToElement(new { id = "sub-1" }));

        Assert.True(_extension.Roster!.TryGet("sub-1", out var child));
        Assert.Equal(AgentStatus.Gone, child.Status);
    }

    [Fact]
    public async Task SessionShutdown_DisposesRosterEntry()
    {
        await _extension.InitializeAsync(_api);
        var roster = _extension.Roster!;

        await _api.RaiseAsync(ExtensionEventNames.SessionShutdown);

        Assert.False(roster.TryGet("root-session", out _));
    }

    [Fact]
    public async Task QueuedMessage_ReplaysOnResume()
    {
        await _extension.InitializeAsync(_api);
        _extension.Roster!.Register(TestAgents.Agent("child-1", parent: "root-session"));
        _extension.Roster!.UpdateStatus("child-1", AgentStatus.Passivated);

        // A message to the passivated child is queued + persisted, not injected.
        var sent = await _extension.Router!.SendAsync("root-session", ["child-1"], "queued hello", AgentMessageDelivery.Steer);
        Assert.Equal(AgentMessageStatus.Queued, Assert.Single(sent.Recipients).Status);
        Assert.Empty(_api.SentMessages);
        Assert.Single(await _extension.Router.Store.LoadAsync());

        // Child resumes → replay delivers live and drains the outbox.
        _extension.Roster.UpdateStatus("child-1", AgentStatus.Running);
        await _extension.Router.ReplayAsync();

        Assert.Empty(await _extension.Router.Store.LoadAsync());
        Assert.Contains(_api.EmittedClientEvents, e => e.EventName == AgentMessagingEventNames.AgentMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }
}
