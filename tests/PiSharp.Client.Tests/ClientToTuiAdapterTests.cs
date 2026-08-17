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

    [Fact]
    public void ToHarnessEvent_MapsSessionMetrics()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("session_metrics", new
            {
                cwd = "test-repo",
                gitBranch = "main",
                inputTokens = 120,
                outputTokens = 450,
                cacheTokens = 80,
                totalTokens = 650,
                totalCost = 0.025m,
                contextPercent = 33.3,
                contextPercentKnown = true,
                contextWindow = 200000,
                autoCompact = true
            }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var own = Assert.IsType<AgentHarnessEvent.Own>(harnessEvent);
        var metrics = Assert.IsType<AgentHarnessOwnEvent.SessionMetrics>(own.Event);
        Assert.Equal("test-repo", metrics.Cwd);
        Assert.Equal("main", metrics.GitBranch);
        Assert.Equal(120, metrics.InputTokens);
        Assert.Equal(450, metrics.OutputTokens);
        Assert.Equal(80, metrics.CacheTokens);
        Assert.Equal(650, metrics.TotalTokens);
        Assert.Equal(0.025m, metrics.TotalCost);
        Assert.Equal(33.3, metrics.ContextPercent);
        Assert.True(metrics.ContextPercentKnown);
        Assert.Equal(200000, metrics.ContextWindow);
        Assert.True(metrics.AutoCompact);
    }

    [Fact]
    public void ToHarnessEvent_MapsExtensionLoadStatus()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("extension_load_status", new
            {
                total = 10,
                active = 2,
                blockingActive = 1,
                ready = 7,
                failed = 1,
                failures = new[] { new { path = "bad.js", diagnostic = "syntax error" } }
            }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var own = Assert.IsType<AgentHarnessEvent.Own>(harnessEvent);
        var status = Assert.IsType<AgentHarnessOwnEvent.ExtensionLoadStatusUpdate>(own.Event);
        Assert.Equal(10, status.Total);
        Assert.Equal(2, status.Active);
        Assert.Equal(1, status.BlockingActive);
        Assert.Equal(7, status.Ready);
        Assert.Equal(1, status.Failed);
        Assert.NotNull(status.Failures);
        Assert.Single(status.Failures);
        Assert.Equal("bad.js", status.Failures[0].Path);
    }

    [Fact]
    public void ToHarnessEvent_MapsModifiedFiles()
    {
        var envelope = ServerEventEnvelope.FromFlat(
            "srv-1",
            1,
            AgentSessionEvent.FromServer("modified_files", new
            {
                files = new[] { "src/Main.cs", "README.md" }
            }));

        var harnessEvent = ClientToTuiAdapter.ToHarnessEvent(envelope);

        var own = Assert.IsType<AgentHarnessEvent.Own>(harnessEvent);
        var modified = Assert.IsType<AgentHarnessOwnEvent.ModifiedFilesUpdate>(own.Event);
        Assert.Equal(2, modified.Files.Count);
        Assert.Equal("src/Main.cs", modified.Files[0]);
        Assert.Equal("README.md", modified.Files[1]);
    }

    [Fact]
    public void ToSessionSnapshot_MapsEnrichedMetadata()
    {
        var serverSnapshot = new ServerSessionSnapshot(
            "session-abc",
            "session.jsonl",
            "Test Session",
            [],
            Footer: new ServerFooterSnapshot("my-dir", "feat/pushed", 10, 20, 5, 35, 0.001m, 12.5, true, 64000, false),
            ModifiedFiles: ["fileA.cs", "fileB.cs"],
            ExtensionLoadStatus: new PiSharp.Runtime.ExtensionLoadSummary(2, 0, 0, 2, 0, []),
            Commands: ["/run", "/stop"]);

        var tuiSnapshot = ClientToTuiAdapter.ToSessionSnapshot(serverSnapshot);

        Assert.Equal("session-abc", tuiSnapshot.SessionId);
        Assert.Equal("Test Session", tuiSnapshot.SessionName);
        Assert.NotNull(tuiSnapshot.FooterSnapshot);
        Assert.Equal("my-dir", tuiSnapshot.FooterSnapshot.Cwd);
        Assert.Equal("feat/pushed", tuiSnapshot.FooterSnapshot.GitBranch);
        Assert.NotNull(tuiSnapshot.ModifiedFiles);
        Assert.Equal(2, tuiSnapshot.ModifiedFiles.Count);
        Assert.NotNull(tuiSnapshot.ExtensionLoadStatus);
        Assert.Equal(2, tuiSnapshot.ExtensionLoadStatus.Ready);
        Assert.NotNull(tuiSnapshot.Commands);
        Assert.Equal(2, tuiSnapshot.Commands.Count);
    }
}
