using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using Xunit;

namespace PiSharp.Client.Tests;

/// <summary>
/// Wire-shape → harness-event mapping for the flat <c>system_message</c> lane
/// (server-originated startup-check/self-update lines rendered as TUI system rows).
/// </summary>
public sealed class ClientToTuiAdapterTests
{
    [Fact]
    public void ToHarnessEvent_MapsSystemMessageToOwnEvent()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("system_message", new { text = "Self-update check complete" }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var own = Assert.IsType<AgentHarnessEvent.Own>(harnessEvent);
        var system = Assert.IsType<AgentHarnessOwnEvent.SystemMessage>(own.Event);
        Assert.Equal("Self-update check complete", system.Text);
        Assert.False(system.IsError);
    }

    [Fact]
    public void ToHarnessEvent_DropsEmptySystemMessage()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("system_message", new { text = "  " }));

        Assert.Null(ClientToTuiAdapter.ToHarnessEvent(envelope));
    }

    [Fact]
    public void ToHarnessEvent_MapsSystemMessageIsErrorFlag()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("system_message", new { text = "Update failed", isError = true }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var system = Assert.IsType<AgentHarnessOwnEvent.SystemMessage>(
            Assert.IsType<AgentHarnessEvent.Own>(harnessEvent).Event);
        Assert.Equal("Update failed", system.Text);
        Assert.True(system.IsError);
    }

    [Fact]
    public void ToHarnessEvent_MapsPackagesChangedToSystemRow()
    {
        // The server emits package changes under ExtensionEventNames.PackagesChanged ("extensions_changed").
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("extensions_changed", new { added = new[] { "my-ext" }, removed = Array.Empty<string>(), updated = Array.Empty<string>() }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var system = Assert.IsType<AgentHarnessOwnEvent.SystemMessage>(
            Assert.IsType<AgentHarnessEvent.Own>(harnessEvent).Event);
        Assert.Contains("my-ext", system.Text);
    }

    [Fact]
    public void ToHarnessEvent_MapsSkillsChangedToSystemRow()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("skills_changed", new { added = new[] { "code-reviewer" }, removed = new[] { "old-skill" }, updated = Array.Empty<string>() }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var system = Assert.IsType<AgentHarnessOwnEvent.SystemMessage>(
            Assert.IsType<AgentHarnessEvent.Own>(harnessEvent).Event);
        Assert.Contains("code-reviewer", system.Text);
        Assert.Contains("old-skill", system.Text);
    }

    [Fact]
    public void ToHarnessEvent_MapsThemeChangedToSystemRow()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("theme_changed", new { name = "ocean", document = new { } }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var system = Assert.IsType<AgentHarnessOwnEvent.SystemMessage>(
            Assert.IsType<AgentHarnessEvent.Own>(harnessEvent).Event);
        Assert.Contains("ocean", system.Text);
    }

    [Fact]
    public void ToHarnessEvent_DropsListChangedWithNoEntries()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("skills_changed", new { added = Array.Empty<string>(), removed = Array.Empty<string>(), updated = Array.Empty<string>() }));

        Assert.Null(ClientToTuiAdapter.ToHarnessEvent(envelope));
    }
}
