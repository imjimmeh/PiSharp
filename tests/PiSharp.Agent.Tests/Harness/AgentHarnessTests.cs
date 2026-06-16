using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Agent.Tests.Harness;

public sealed class AgentHarnessTests
{
    [Fact]
    public async Task PromptExpandsSkillCommandBeforePersistingUserMessage()
    {
        var session = CreateSession();
        var skill = new Skill("example", "Example skill", "Example content", "/repo/skills/example/SKILL.md");
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, [], Skills: [skill]));

        await harness.PromptAsync("/skill:example apply this");

        var user = Assert.Single((await session.BuildContextAsync()).Messages.OfType<UserMessage>());
        var text = Assert.IsType<TextContent>(Assert.Single(user.Content)).Text;
        Assert.Contains("<skill name=\"example\"", text);
        Assert.Contains("Example content", text);
        Assert.EndsWith("apply this", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptLeavesUnknownSkillCommandUnchanged()
    {
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));

        await harness.PromptAsync("/skill:missing keep raw");

        var user = Assert.Single((await session.BuildContextAsync()).Messages.OfType<UserMessage>());
        Assert.Equal("/skill:missing keep raw", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public async Task PromptWritesUserAndAssistantToSession()
    {
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));
        var result = await harness.PromptAsync("hello");
        var entries = await session.GetEntriesAsync();

        Assert.Equal("ok", result.Content.OfType<TextContent>().FirstOrDefault()?.Text);
        Assert.Collection(entries.OfType<MessageEntry>(),
            entry => Assert.IsType<UserMessage>(entry.Message),
            entry => Assert.IsType<AssistantMessage>(entry.Message));
    }

    [Fact]
    public async Task PromptPersistsUserAndVisibleErrorWhenProviderThrowsBeforeAssistantStart()
    {
        var session = CreateSession();
        var model = new ModelDescriptor("custom", "broken-model", "custom-api");
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, model, ThrowingStream, FakeCompletion, []));

        var result = await harness.PromptAsync("hello");
        var entries = (await session.GetEntriesAsync()).OfType<MessageEntry>().ToArray();

        Assert.Equal("error", result.StopReason);
        Assert.Equal("custom", result.Provider);
        Assert.Equal("broken-model", result.Model);
        Assert.Contains("provider exploded", result.ErrorMessage);
        Assert.Contains(result.Content.OfType<TextContent>(), content => content.Text.Contains("Provider error for custom/broken-model (custom-api): provider exploded", StringComparison.Ordinal));
        Assert.Collection(entries,
            entry => Assert.IsType<UserMessage>(entry.Message),
            entry =>
            {
                var assistant = Assert.IsType<AssistantMessage>(entry.Message);
                Assert.Equal("error", assistant.StopReason);
                Assert.Contains("provider exploded", assistant.ErrorMessage);
            });
    }

    [Fact]
    public async Task HarnessRejectsPromptWhileBusy()
    {
        var session = CreateSession();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), BlockingStream(gate.Task), FakeCompletion, []));
        var _ = harness.PromptAsync("first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PromptAsync("second"));
        gate.SetResult();
    }

    [Fact]
    public async Task PromptPassesConfiguredSystemPromptToProvider()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var session = CreateSession();
        var options = new SystemPromptBuildOptions(
            Cwd: "/repo",
            CurrentDate: new DateOnly(2026, 5, 26),
            CustomPrompt: "CUSTOM PROMPT");
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [],
            SystemPrompt: options));

        await harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.StartsWith("CUSTOM PROMPT", observed.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptUsesDefaultSystemPromptWhenNoOptionsAreConfigured()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            []));

        await harness.PromptAsync("hello");

        Assert.Contains("You are an expert coding assistant operating inside pi", observed!.SystemPrompt);
    }

    [Fact]
    public async Task PromptListsActiveToolDescriptionsEvenWhenPromptSnippetMissing()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var tool = new PromptlessTool("extension_search", "Search project documentation from an extension");
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [tool]));

        await harness.PromptAsync("what tools are available?");

        Assert.Contains(observed!.Tools!, available => available.Name == "extension_search");
        Assert.Contains("- extension_search: Search project documentation from an extension", observed.SystemPrompt);
    }

    [Fact]
    public async Task BeforeAgentStartEventIncludesSystemPromptAndResources()
    {
        var observedEvents = new List<AgentHarnessEvent>();
        var tool = new CountingTool("read");
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            FakeStream("ok"),
            FakeCompletion,
            [tool],
            SystemPrompt: new SystemPromptBuildOptions(Cwd: "/repo", CustomPrompt: "REAL PROMPT")));
        using var _ = harness.Subscribe((evt, _) => { observedEvents.Add(evt); return Task.CompletedTask; });

        await harness.PromptAsync("hello");

        var before = Assert.IsType<AgentHarnessOwnEvent.BeforeAgentStart>(Assert.IsType<AgentHarnessEvent.Own>(observedEvents.First(evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeAgentStart })).Event);
        Assert.StartsWith("REAL PROMPT", before.SystemPrompt, StringComparison.Ordinal);
        var resources = Assert.IsAssignableFrom<IReadOnlyList<IAgentTool>>(before.Resources);
        Assert.Contains(resources, resource => resource.Name == "read");
    }

    [Fact]
    public async Task BeforeAgentStartExtensionCanModifySystemPromptAndInjectMessages()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.BeforeAgentStart, (evt, _) =>
        {
            evt.ModifyBeforeAgentStart("MUTATED PROMPT", [AgentMessages.User("injected")]);
            return Task.CompletedTask;
        });
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [],
            Extensions: registry));

        await harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.Equal("MUTATED PROMPT", observed.SystemPrompt);
        Assert.Collection(observed.Messages.OfType<UserMessage>(),
            message => Assert.Equal("injected", Assert.IsType<TextContent>(Assert.Single(message.Content)).Text),
            message => Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(message.Content)).Text));
    }

    [Fact]
    public void QueueNextTurnRejectsToolResultMessages()
    {
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            FakeStream("ok"),
            FakeCompletion,
            []));

        var exception = Assert.Throws<InvalidOperationException>(() => harness.QueueNextTurn(UnsafeToolResult()));

        Assert.Contains("ToolResultMessage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SteerRejectsToolResultMessages()
    {
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            FakeStream("ok"),
            FakeCompletion,
            []));

        var exception = Assert.Throws<InvalidOperationException>(() => harness.Steer(UnsafeToolResult()));

        Assert.Contains("ToolResultMessage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowUpRejectsToolResultMessages()
    {
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            FakeStream("ok"),
            FakeCompletion,
            []));

        var exception = Assert.Throws<InvalidOperationException>(() => harness.FollowUp(UnsafeToolResult()));

        Assert.Contains("ToolResultMessage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeforeAgentStartRejectsInjectedToolResultMessages()
    {
        var session = CreateSession();
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.BeforeAgentStart, (evt, _) =>
        {
            evt.ModifyBeforeAgentStart(messages: [UnsafeToolResult()]);
            return Task.CompletedTask;
        });
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            FakeStream("ok"),
            FakeCompletion,
            [],
            Extensions: registry));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.PromptAsync("hello"));

        Assert.Contains("ToolResultMessage", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await session.GetEntriesAsync());
    }

    [Fact]
    public async Task BeforePromptRenderExtensionCanPatchDocumentBeforeRender()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.BeforePromptRender, (evt, _) =>
        {
            var payload = Assert.IsType<PromptDocumentHookPayload>(evt.Payload);
            Assert.Contains(payload.Sections, section => section.Id == "skills.available");
            evt.ModifyPromptDocument(new PromptDocumentPatch(ReplaceSections: [
                new PromptDocumentSectionPatch("skills.available", "PATCHED SKILLS", Slot: "skills", Kind: "skills", ContentType: "raw")
            ]));
            return Task.CompletedTask;
        });
        var skills = new[] { new Skill("alpha", "Alpha skill", "body", "/repo/alpha/SKILL.md") };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [new CountingTool("read")],
            Skills: skills,
            Extensions: registry));

        await harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.Contains("PATCHED SKILLS", observed!.SystemPrompt);
        Assert.DoesNotContain("<name>alpha</name>", observed.SystemPrompt);
        var skillsSection = Assert.Single(harness.LastPromptDocument!.Sections, section => section.Id == "skills.available");
        Assert.Equal("PATCHED SKILLS", Assert.IsType<RawPromptContent>(skillsSection.Content).Text);
    }

    [Fact]
    public async Task BeforeAgentStartStillRunsAfterPromptDocumentHook()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:document", ExtensionEventNames.BeforePromptRender, (evt, _) =>
        {
            evt.ModifyPromptDocument(new PromptDocumentPatch(AppendSections: [
                new PromptDocumentSectionPatch("document-hook", "DOCUMENT HOOK", Slot: "instructions")
            ]));
            return Task.CompletedTask;
        });
        registry.RegisterHandler("extension:legacy", ExtensionEventNames.BeforeAgentStart, (evt, _) =>
        {
            var before = Assert.IsType<AgentHarnessOwnEvent.BeforeAgentStart>(Assert.IsType<AgentHarnessEvent.Own>(evt.OriginalEvent).Event);
            Assert.Contains("DOCUMENT HOOK", before.SystemPrompt);
            evt.ModifyBeforeAgentStart(before.SystemPrompt + "\nLEGACY HOOK");
            return Task.CompletedTask;
        });
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [],
            Extensions: registry));

        await harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.Contains("DOCUMENT HOOK", observed!.SystemPrompt);
        Assert.Contains("LEGACY HOOK", observed.SystemPrompt);
        Assert.DoesNotContain("LEGACY HOOK", MarkdownSystemPromptRenderer.Default.Render(harness.LastPromptDocument!));
    }

    [Fact]
    public async Task PromptUsesComposerOutputAndTracksDebugDocument()
    {
        AgentContext? observed = null;
        AgentStreamAsync stream = (_, context, _, _) =>
        {
            observed = context;
            return StreamHelper("ok");
        };
        var composer = new TestComposer();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [],
            SystemPromptComposer: composer));

        await harness.PromptAsync("hello");

        Assert.Equal("COMPOSED", observed!.SystemPrompt);
        Assert.NotNull(harness.LastPromptDocument);
        Assert.Equal("test.section", Assert.Single(harness.LastPromptDocument!.Sections).Id);
    }

    [Fact]
    public async Task PromptComposerReceivesActiveToolsPerTurn()
    {
        var included = new CountingTool("included");
        var excluded = new CountingTool("excluded");
        var composer = new CapturingComposer();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            FakeStream("ok"),
            FakeCompletion,
            [included, excluded],
            ActiveToolNames: ["included"],
            SystemPromptComposer: composer));

        await harness.PromptAsync("hello");

        Assert.Equal(["included"], composer.SeenToolNames);
    }

    [Fact]
    public async Task PromptPassesConfiguredStreamOptionsToProvider()
    {
        AgentStreamOptions? observed = null;
        AgentStreamAsync stream = (_, _, options, _) =>
        {
            observed = options;
            return StreamHelper("ok");
        };
        var configured = new AgentStreamOptions(ApiKey: "key", SessionId: "session-id", Transport: "stdio");
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, [], StreamOptions: configured));

        await harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.Equal("key", observed.ApiKey);
        Assert.Equal("session-id", observed.SessionId);
        Assert.Equal("stdio", observed.Transport);
    }

    [Fact]
    public async Task NullActiveToolNamesMakesAllToolsAvailable()
    {
        var tool = new CountingTool("read");
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), ToolThenStopStream("read"), FakeCompletion, [tool]));

        await harness.PromptAsync("use tool");

        Assert.Equal(1, tool.CallCount);
        var context = await session.BuildContextAsync();
        var result = Assert.Single(context.Messages.OfType<ToolResultMessage>());
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ActiveToolNamesFiltersAvailableTools()
    {
        var included = new CountingTool("included");
        var excluded = new CountingTool("excluded");
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), ToolThenStopStream("excluded"), FakeCompletion, [included, excluded], ActiveToolNames: ["included"]));

        await harness.PromptAsync("use tool");

        Assert.Equal(0, excluded.CallCount);
        var context = await session.BuildContextAsync();
        var result = Assert.Single(context.Messages.OfType<ToolResultMessage>());
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task FollowUpQueueAddsSecondTurn()
    {
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));
        harness.FollowUp(AgentMessages.User("follow-up"));

        await harness.PromptAsync("hello");

        var context = await session.BuildContextAsync();
        Assert.Equal(2, context.Messages.OfType<UserMessage>().Count());
        Assert.Equal(2, context.Messages.OfType<AssistantMessage>().Count());
        Assert.Contains(context.Messages.OfType<UserMessage>(), message => ((TextContent)message.Content[0]).Text == "follow-up");
    }

    [Fact]
    public async Task QueueNextTurnPrependsMessageToPrompt()
    {
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));
        harness.QueueNextTurn(AgentMessages.User("preface"));

        await harness.PromptAsync("hello");

        var users = (await session.BuildContextAsync()).Messages.OfType<UserMessage>().ToArray();
        Assert.Equal(["preface", "hello"], users.Select(message => ((TextContent)message.Content[0]).Text));
    }

    [Fact]
    public async Task PromptPersistsImagesInUserMessage()
    {
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));

        await harness.PromptAsync("see", [new ImageContent("image/png", "abc")]);

        var user = Assert.Single((await session.BuildContextAsync()).Messages.OfType<UserMessage>());
        Assert.Contains(user.Content, content => content is ImageContent { MediaType: "image/png", Data: "abc" });
    }

    [Fact]
    public async Task RuntimeRegisteredToolIsAvailableOnNextTurnAndRemovableBySource()
    {
        var tool = new CountingTool("dynamic");
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), ToolThenStopStream("dynamic"), FakeCompletion, []));

        using var registration = harness.RegisterTool("extension:test", tool);
        await harness.PromptAsync("use tool");
        var removed = harness.UnregisterToolsBySource("extension:test");

        Assert.Equal(1, tool.CallCount);
        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task ToolMiddlewareReceivesInputAndCanModifyResult()
    {
        IReadOnlyDictionary<string, object?>? beforeInput = null;
        var registry = new ExtensionRegistry();
        registry.RegisterMiddleware("extension:test", (context, _, _) =>
        {
            if (context.BeforeToolCall is not null)
            {
                var own = Assert.IsType<AgentHarnessEvent.Own>(context.Event.OriginalEvent);
                beforeInput = Assert.IsType<AgentHarnessOwnEvent.ToolCall>(own.Event).Arguments;
            }
            if (context.AfterToolCall is not null)
            {
                context.ModifyToolResult([new TextContent("modified")], new { changed = true }, isError: true);
            }
            return Task.CompletedTask;
        });
        var tool = new CapturingTool("read");
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            ToolThenStopStream("read", "{\"path\":\"README.md\"}"),
            FakeCompletion,
            [tool],
            Extensions: registry));

        await harness.PromptAsync("use tool");

        Assert.NotNull(beforeInput);
        Assert.Equal("README.md", Assert.IsType<JsonElement>(beforeInput!["path"]).GetString());
        var result = Assert.Single((await session.BuildContextAsync()).Messages.OfType<ToolResultMessage>());
        Assert.True(result.IsError);
        Assert.Equal("modified", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task ToolMiddlewareCanBlockExecutionWithoutPublishingToolOwnEventsToObservers()
    {
        var registry = new ExtensionRegistry();
        var middlewareRan = false;
        var handlerSawToolCall = false;
        registry.RegisterHandler("extension:test", ExtensionEventNames.ToolCall, (_, _) =>
        {
            handlerSawToolCall = true;
            return Task.CompletedTask;
        });
        registry.RegisterMiddleware("extension:test", (context, _, _) =>
        {
            middlewareRan = true;
            context.Blocked = true;
            context.BlockReason = "blocked by middleware";
            return Task.CompletedTask;
        });
        var tool = new CountingTool("read");
        var session = CreateSession();
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            session,
            new ModelDescriptor("test", "test", "test"),
            ToolThenStopStream("read"),
            FakeCompletion,
            [tool],
            Extensions: registry));
        var listenerSawToolCall = false;
        using var _ = harness.Subscribe((evt, _) =>
        {
            if (evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.ToolCall })
            {
                listenerSawToolCall = true;
            }

            return Task.CompletedTask;
        });

        await harness.PromptAsync("use tool");

        Assert.True(middlewareRan);
        Assert.False(handlerSawToolCall);
        Assert.False(listenerSawToolCall);
        Assert.Equal(0, tool.CallCount);
        var result = Assert.Single((await session.BuildContextAsync()).Messages.OfType<ToolResultMessage>());
        Assert.True(result.IsError);
        Assert.Equal("blocked by middleware", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task CompactWritesCompactionEntryToSession()
    {
        var session = CreateSession();
        await session.AppendMessageAsync(AgentMessages.User(new string('x', 1000)));
        await session.AppendMessageAsync(AgentMessages.Assistant(new string('y', 1000)));
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));

        await harness.CompactAsync();

        var compaction = Assert.Single((await session.GetEntriesAsync()).OfType<CompactionEntry>());
        Assert.Equal("summarized", compaction.Summary);
        Assert.True(compaction.TokensBefore > 0);
    }

    [Fact]
    public async Task CompactEmitsSessionCompactionEvents()
    {
        var session = CreateSession();
        await session.AppendMessageAsync(AgentMessages.User(new string('x', 1000)));
        await session.AppendMessageAsync(AgentMessages.Assistant(new string('y', 1000)));
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));
        var events = new List<AgentHarnessEvent>();
        using var _ = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        await harness.CompactAsync("keep decisions");

        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.CompactionStart });
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeCompact });
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionCompact });
        var end = Assert.IsType<AgentHarnessOwnEvent.CompactionEnd>(Assert.IsType<AgentHarnessEvent.Own>(events.Last()).Event);
        Assert.Equal("manual", end.Reason);
        Assert.False(end.Aborted);
        Assert.False(end.WillRetry);
        Assert.Null(end.ErrorMessage);
    }

    [Fact]
    public async Task NavigateTreeEmitsSessionTreeEvents()
    {
        var session = CreateSession();
        var root = await session.AppendMessageAsync(AgentMessages.User("root"));
        var target = await session.AppendMessageAsync(AgentMessages.Assistant("target"));
        await session.MoveToAsync(root);
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));
        var events = new List<AgentHarnessEvent>();
        using var _ = harness.Subscribe((evt, _) => { events.Add(evt); return Task.CompletedTask; });

        await harness.NavigateTreeAsync(target);

        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionBeforeTree });
        Assert.Contains(events, evt => evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionTree });
    }

    [Fact]
    public async Task NavigateTreeWithSummaryWritesBranchSummary()
    {
        var session = CreateSession();
        var root = await session.AppendMessageAsync(AgentMessages.User("root"));
        var oldLeaf = await session.AppendMessageAsync(AgentMessages.Assistant("old branch"));
        await session.MoveToAsync(root);
        var target = await session.AppendMessageAsync(AgentMessages.Assistant("target branch"));
        await session.MoveToAsync(oldLeaf);
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream("ok"), FakeCompletion, []));

        await harness.NavigateTreeAsync(target, summarize: true);

        var summary = Assert.Single((await session.GetEntriesAsync()).OfType<BranchSummaryEntry>());
        Assert.Equal(oldLeaf, summary.FromId);
        Assert.Contains("summarized", summary.Summary);
    }

    [Fact]
    public async Task SystemPromptComposerFactoryIsResolvedForEachPrompt()
    {
        var seenPrompts = new List<string>();
        var factoryCalls = 0;
        AgentStreamAsync stream = (_, context, _, _) => { seenPrompts.Add(context.SystemPrompt); return StreamHelper("ok"); };
        var harness = new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(
            CreateSession(),
            new ModelDescriptor("test", "test", "test"),
            stream,
            FakeCompletion,
            [],
            SystemPromptComposerFactory: () => new FixedPromptComposer($"PROMPT {++factoryCalls}")));

        await harness.PromptAsync("first");
        await harness.PromptAsync("second");

        Assert.Equal(["PROMPT 1", "PROMPT 2"], seenPrompts);
    }

    private static Session<JsonlSessionMetadata> CreateSession()
        => new(new MemorySessionStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, "cwd", "test.jsonl")));

    private static AgentStreamAsync FakeStream(string text)
    {
        AgentStreamAsync stream = (_, _, _, _) => StreamHelper(text);
        return stream;
    }

    private static IAsyncEnumerable<AssistantMessageEvent> ThrowingStream(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken cancellationToken)
        => ThrowingStreamHelper();

    private static AgentStreamAsync BlockingStream(Task waitFor)
    {
        AgentStreamAsync stream = (_, _, _, _) => BlockingStreamHelper(waitFor);
        return stream;
    }

    private static AgentStreamAsync ToolThenStopStream(string toolName)
    {
        AgentStreamAsync stream = (_, context, _, _) => StreamToolThenStop(toolName, context);
        return stream;
    }

    private static AgentStreamAsync ToolThenStopStream(string toolName, string argsJson)
    {
        AgentStreamAsync stream = (_, context, _, _) => StreamToolThenStop(toolName, context, argsJson);
        return stream;
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("summarized"));

    private static ToolResultMessage UnsafeToolResult()
        => AgentMessages.ToolResult("call-orphan", "read", "orphan result");

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        var msg = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(msg);
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(msg);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> ThrowingStreamHelper()
    {
        await Task.Yield();
        throw new InvalidOperationException("provider exploded");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> BlockingStreamHelper(Task waitFor)
    {
        await waitFor;
        var msg = new AssistantMessage([new TextContent("ok")], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(msg);
        yield return new AssistantMessageEvent.Done(msg);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamToolThenStop(string toolName, AgentContext context, string argsJson = "{}")
    {
        await Task.Yield();
        AssistantMessage msg;
        if (context.Messages.OfType<ToolResultMessage>().Any())
        {
            msg = new AssistantMessage([new TextContent("done")], StopReason: "stop");
        }
        else
        {
            using var args = JsonDocument.Parse(argsJson);
            msg = new AssistantMessage([new ToolCallContent("tool-1", toolName, args.RootElement.Clone())], StopReason: "tool_use");
        }

        yield return new AssistantMessageEvent.Start(msg);
        yield return new AssistantMessageEvent.Done(msg);
    }

    private sealed class FixedPromptComposer(string prompt) : ISystemPromptComposer
    {
        public SystemPromptDocument Compose(SystemPromptCompositionContext context)
            => new([new PromptSection("fixed", PromptSectionKind.Extension, new RawPromptContent(prompt), new PromptPlacement("header"))], []);

        public string Render(SystemPromptDocument document)
            => MarkdownSystemPromptRenderer.Default.Render(document);

        public string Build(SystemPromptCompositionContext context) => prompt;
    }

    private sealed class TestComposer : ISystemPromptComposer
    {
        public SystemPromptDocument Compose(SystemPromptCompositionContext context)
            => new([new PromptSection("test.section", PromptSectionKind.Extension, new RawPromptContent("body"), new PromptPlacement("header"))], []);

        public string Render(SystemPromptDocument document) => "COMPOSED";

        public string Build(SystemPromptCompositionContext context) => "COMPOSED";
    }

    private sealed class CapturingComposer : ISystemPromptComposer
    {
        public IReadOnlyList<string> SeenToolNames { get; private set; } = [];

        public SystemPromptDocument Compose(SystemPromptCompositionContext context)
        {
            SeenToolNames = context.SelectedToolNames;
            return new SystemPromptDocument([], []);
        }

        public string Render(SystemPromptDocument document) => string.Empty;

        public string Build(SystemPromptCompositionContext context)
        {
            SeenToolNames = context.SelectedToolNames;
            return string.Empty;
        }
    }

    private sealed class CapturingTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => ToolExecutionMode.Sequential;
        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
            => Task.FromResult(new AgentToolResult<object?>([new TextContent("original")], new { original = true }));
    }

    private sealed class PromptlessTool(string name, string description) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => description;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;
        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
            => Task.FromResult(new AgentToolResult<object?>([new TextContent($"{name} result")], new { name }));
    }

    private sealed class CountingTool(string name) : IAgentTool
    {
        public int CallCount { get; private set; }
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;
        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
        {
            CallCount++;
            return Task.FromResult(new AgentToolResult<object?>([new TextContent($"{name} result")], new { name }));
        }
    }
}
