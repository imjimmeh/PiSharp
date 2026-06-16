using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Tools;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class ExtensionRegistryTests
{
    [Fact]
    public void RegisterCommandPreservesDuplicateCommandNamesFromDifferentSources()
    {
        var registry = new ExtensionRegistry();

        registry.RegisterCommand("extension:first", new ExtensionCommandRegistration("schedule", "first", (_, _) => Task.CompletedTask));
        registry.RegisterCommand("extension:second", new ExtensionCommandRegistration("schedule", "second", (_, _) => Task.CompletedTask));

        Assert.Equal(2, registry.Commands.Count(command => command.Value.Name == "schedule"));
    }

    [Fact]
    public void RegisterShortcutStoresKeysAndDescription()
    {
        var registry = new ExtensionRegistry();

        registry.RegisterShortcut("extension:test", new ExtensionShortcutRegistration("ctrl+k", "Run test", (_, _) => Task.CompletedTask));

        var shortcut = Assert.Single(registry.Shortcuts);
        Assert.Equal("extension:test", shortcut.SourceId);
        Assert.Equal("ctrl+k", shortcut.Value.Keys);
        Assert.Equal("Run test", shortcut.Value.Description);
    }

    [Fact]
    public void UnregisterBySourceRemovesOnlyMatchingRegistrations()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterTool("extension:a", Tool("a"));
        registry.RegisterTool("extension:b", Tool("b"));
        registry.RegisterHandler("extension:a", ExtensionEventNames.MessageEnd, (_, _) => Task.CompletedTask);

        var removed = registry.UnregisterBySource("extension:a");

        Assert.Equal(2, removed);
        Assert.DoesNotContain(registry.Tools, tool => tool.SourceId == "extension:a");
        Assert.Single(registry.Tools);
        Assert.Equal("extension:b", registry.Tools[0].SourceId);
        Assert.Empty(registry.Handlers);
    }

    [Fact]
    public void RegisterToolRejectsDuplicateByDefault()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterTool("extension:a", Tool("same"));

        Assert.Throws<InvalidOperationException>(() => registry.RegisterTool("extension:b", Tool("same")));
    }

    [Fact]
    public void RegisterToolWaitsForChangedHandlersInSubscriptionOrder()
    {
        var registry = new ExtensionRegistry();
        var observed = new List<string>();
        registry.Changed += async (_, _) =>
        {
            observed.Add("first:start");
            await Task.Delay(25);
            observed.Add("first:end");
        };
        registry.Changed += (_, _) =>
        {
            observed.Add("second");
            return Task.CompletedTask;
        };

        registry.RegisterTool("extension:test", Tool("ordered"));

        Assert.Equal(["first:start", "first:end", "second"], observed);
    }

    [Fact]
    public void ChangedHandlerFailureDoesNotBlockLaterHandlersOrMutation()
    {
        var stream = new ExtensionRegistryChangeStream();
        var registry = new ExtensionRegistry(stream);
        var observed = new List<string>();
        registry.Changed += (_, _) => throw new InvalidOperationException("boom");
        registry.Changed += (_, _) =>
        {
            observed.Add("second");
            return Task.CompletedTask;
        };

        registry.RegisterTool("extension:test", Tool("resilient"));

        Assert.Contains("resilient", registry.Tools.Select(tool => tool.Value.Name));
        Assert.Equal(["second"], observed);
        var failure = Assert.Single(stream.Failures);
        Assert.Equal(ExtensionRegistryChangeKind.Added, failure.Change.Kind);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public async Task ChangeStreamPublishAsyncHonorsCancellationBeforeDelivery()
    {
        var stream = new ExtensionRegistryChangeStream();
        var delivered = false;
        stream.Subscribe((_, _) =>
        {
            delivered = true;
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => stream.PublishAsync(new ExtensionRegistryChange(ExtensionRegistryChangeKind.Added, "extension:test", "tool", "tool:test"), cts.Token));
        Assert.False(delivered);
    }

    [Fact]
    public void RegisterToolOverridePublishesAddReplaceRemoveAndRestoreInOrder()
    {
        var registry = new ExtensionRegistry();
        var changes = new List<ExtensionRegistryChangeKind>();
        registry.Changed += (change, _) => { changes.Add(change.Kind); return Task.CompletedTask; };
        registry.RegisterTool("extension:base", Tool("same"));
        var handle = registry.RegisterTool("extension:override", Tool("same"), ExtensionOverridePolicy.Override);

        Assert.Equal("extension:override", registry.Tools.Single().SourceId);
        handle.Dispose();

        Assert.Equal("extension:base", registry.Tools.Single().SourceId);
        Assert.Equal([
            ExtensionRegistryChangeKind.Added,
            ExtensionRegistryChangeKind.Replaced,
            ExtensionRegistryChangeKind.Removed,
            ExtensionRegistryChangeKind.Restored
        ], changes);
    }

    [Fact]
    public void RegisterSkillRejectsDuplicateByDefault()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:a", Skill("same"));

        Assert.Throws<InvalidOperationException>(() => registry.RegisterSkill("extension:b", Skill("same")));
    }

    [Fact]
    public void RegisterSkillOverridePublishesAddReplaceRemoveAndRestoreInOrder()
    {
        var registry = new ExtensionRegistry();
        var changes = new List<ExtensionRegistryChangeKind>();
        registry.Changed += (change, _) => { changes.Add(change.Kind); return Task.CompletedTask; };
        registry.RegisterSkill("extension:base", Skill("same"));
        var handle = registry.RegisterSkill("extension:override", Skill("same"), ExtensionOverridePolicy.Override);

        Assert.Equal("extension:override", registry.Skills.Single().SourceId);
        handle.Dispose();

        Assert.Equal("extension:base", registry.Skills.Single().SourceId);
        Assert.Equal([
            ExtensionRegistryChangeKind.Added,
            ExtensionRegistryChangeKind.Replaced,
            ExtensionRegistryChangeKind.Removed,
            ExtensionRegistryChangeKind.Restored
        ], changes);
    }

    [Fact]
    public void SourceIdsIncludesSkillRegistrations()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:skills", Skill("dynamic"));

        Assert.Contains("extension:skills", registry.SourceIds);
    }

    [Fact]
    public void DisposingOldHandleDoesNotRemoveActiveOverride()
    {
        var registry = new ExtensionRegistry();
        var old = registry.RegisterTool("extension:base", Tool("same"));
        registry.RegisterTool("extension:override", Tool("same"), ExtensionOverridePolicy.Override);

        old.Dispose();

        Assert.Equal("extension:override", registry.Tools.Single().SourceId);
    }

    [Fact]
    public void RegisterPromptSectionOverrideRestoresPreviousWinnerWhenDisposed()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterPromptSection("extension:base", Section("team-rules"));
        var handle = registry.RegisterPromptSection("extension:override", Section("team-rules"), ExtensionOverridePolicy.Override);

        Assert.Equal("extension:override", registry.PromptSections.Single().SourceId);
        handle.Dispose();

        Assert.Equal("extension:base", registry.PromptSections.Single().SourceId);
    }

    [Fact]
    public void BuiltInToolNamesRequireBuiltInOverridePolicy()
    {
        var registry = new ExtensionRegistry { BuiltInToolNames = new HashSet<string>(StringComparer.Ordinal) { "read" } };

        Assert.Throws<InvalidOperationException>(() => registry.RegisterTool("extension:a", Tool("read")));
        registry.RegisterTool("extension:a", Tool("read"), ExtensionOverridePolicy.OverrideBuiltIn);

        Assert.Equal("extension:a", registry.Tools.Single().SourceId);
    }

    [Fact]
    public async Task DispatchMapsHarnessEventsToTypeScriptCompatibleNames()
    {
        var registry = new ExtensionRegistry();
        var seen = new List<string>();
        registry.RegisterHandler("extension:test", ExtensionEventNames.MessageEnd, (evt, _) => { seen.Add(evt.Name); return Task.CompletedTask; });

        await registry.DispatchAsync(new AgentHarnessEvent.Core(new AgentEvent.MessageEnd(AgentMessages.Assistant("ok"))));

        Assert.Equal([ExtensionEventNames.MessageEnd], seen);
    }

    [Fact]
    public void MapsNewSessionLevelEventsToTypeScriptCompatibleNames()
    {
        Assert.Equal(ExtensionEventNames.CompactionStart, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionStart("manual"))));
        Assert.Equal(ExtensionEventNames.CompactionEnd, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.CompactionEnd("manual", null, false, false, null))));
        Assert.Equal(ExtensionEventNames.AutoRetryStart, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.AutoRetryStart(1, 3, 100, "error"))));
        Assert.Equal(ExtensionEventNames.AutoRetryEnd, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.AutoRetryEnd(true, 1, null))));
        Assert.Equal(ExtensionEventNames.SessionInfoChanged, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionInfoChanged("name"))));
        Assert.Equal(ExtensionEventNames.ThinkingLevelChanged, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelChanged(PiSharp.Abstractions.Options.ThinkingLevel.High))));
    }

    [Fact]
    public void MapsSafeBaselineParityEventsToTypeScriptCompatibleNames()
    {
        Assert.Equal(ExtensionEventNames.Input, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("hello", null, "interactive"))));
        Assert.Equal(ExtensionEventNames.SessionBeforeSwitch, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeSwitch("resume", "target.jsonl", new { Id = "current" }, new { Id = "target" }, CancellationToken.None))));
        Assert.Equal(ExtensionEventNames.SessionBeforeFork, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeFork("entry-1", "at", new { Id = "source" }, new { EntryId = "entry-1" }, CancellationToken.None))));
        Assert.Equal(ExtensionEventNames.SessionShutdown, ExtensionEventMapper.Name(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionShutdown("quit", null, new { Id = "current" }))));
    }

    [Fact]
    public void SafeBaselineParityEventsFlattenToJavaScriptCompatibleSessionEvents()
    {
        Assert.Equal("input", new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("hello", null, "interactive")).ToFlat().Type);
        Assert.Equal("session_before_switch", new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeSwitch("resume", "target.jsonl", new { }, new { }, CancellationToken.None)).ToFlat().Type);
        Assert.Equal("session_before_fork", new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeFork("entry-1", "at", new { }, new { }, CancellationToken.None)).ToFlat().Type);
        Assert.Equal("session_shutdown", new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionShutdown("quit")).ToFlat().Type);
    }

    [Fact]
    public void SafeBaselineParityEventsUseTypedExtensionPayloads()
    {
        var input = ExtensionEventMapper.Map(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("hello", null, "interactive")));
        var inputPayload = Assert.IsType<ExtensionInputEvent>(input.Payload);
        Assert.Equal("hello", inputPayload.Text);
        Assert.Equal("interactive", inputPayload.Source);

        var beforeSwitch = ExtensionEventMapper.Map(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeSwitch("resume", "target.jsonl", new { Id = "current" }, new { Id = "target" }, CancellationToken.None)));
        var switchPayload = Assert.IsType<ExtensionSessionBeforeSwitchEvent>(beforeSwitch.Payload);
        Assert.Equal("resume", switchPayload.Reason);
        Assert.Equal("target.jsonl", switchPayload.TargetSessionFile);

        var beforeFork = ExtensionEventMapper.Map(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeFork("entry-1", "at", new { Id = "source" }, new { EntryId = "entry-1" }, CancellationToken.None)));
        var forkPayload = Assert.IsType<ExtensionSessionBeforeForkEvent>(beforeFork.Payload);
        Assert.Equal("entry-1", forkPayload.EntryId);
        Assert.Equal("at", forkPayload.Position);

        var shutdown = ExtensionEventMapper.Map(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionShutdown("quit", "next.jsonl")));
        var shutdownPayload = Assert.IsType<ExtensionSessionShutdownEvent>(shutdown.Payload);
        Assert.Equal("quit", shutdownPayload.Reason);
        Assert.Equal("next.jsonl", shutdownPayload.TargetSessionFile);
    }

    [Fact]
    public void ExtensionEventStoresInputAndSessionChangeResults()
    {
        var evt = new ExtensionEvent(
            ExtensionEventNames.Input,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input("/hello", null, "interactive")),
            new ExtensionInputEvent("/hello", null, "interactive"));

        evt.TransformInput("say hello");

        Assert.Equal("transform", evt.InputResult!.Action);
        Assert.Equal("say hello", evt.InputResult.Text);

        evt.HandleInput();
        Assert.Equal("handled", evt.InputResult!.Action);

        var sessionEvent = new ExtensionEvent(
            ExtensionEventNames.SessionBeforeSwitch,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeSwitch("resume", "target.jsonl", new { }, new { }, CancellationToken.None)));

        sessionEvent.CancelSessionChange("blocked");

        Assert.True(sessionEvent.SessionChangeResult!.Cancel);
        Assert.Equal("blocked", sessionEvent.SessionChangeResult.Reason);
    }

    private static PiSharp.Agent.Core.Prompting.PromptSection Section(string id)
        => new(id, PiSharp.Agent.Core.Prompting.PromptSectionKind.Extension, new PiSharp.Agent.Core.Prompting.RawPromptContent("body"), new PiSharp.Agent.Core.Prompting.PromptPlacement("footer"));

    private static ExtensionSkillRegistration Skill(string name)
        => new(name, $"{name} skill", "body", $"/repo/{name}/SKILL.md");

    private static IAgentTool Tool(string name)
    {
        using var schema = JsonDocument.Parse("{}");
        return new ExtensionToolRegistration(
            name,
            name,
            name,
            schema.RootElement.Clone(),
            (_, _, _, _) => Task.FromResult(new AgentToolResult<object?>([new TextContent("ok")], new { }))).ToAgentTool();
    }
}
