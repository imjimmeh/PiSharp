using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Resources;
using PiSharp.Ai.Auth;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Parsing;
using PiSharp.Cli.Modes;
using PiSharp.Cli.Tests.Modes;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace PiSharp.Cli.Tests.Commands;

public sealed class SlashCommandRegistryTests
{
    [Fact]
    public void BuiltInInventoryMatchesTypeScriptContract()
        => Assert.Equal(["settings", "model", "models", "scoped-models", "export", "import", "share", "copy", "name", "session", "changelog", "hotkeys", "fork", "clone", "tree", "login", "logout", "new", "compact", "reload", "resume", "quit"], BuiltInSlashCommands.Names);

    [Fact]
    public void BuiltInCatalogFlattensCommandAliasesIntoNames()
        => Assert.Equal(["settings", "model", "models", "scoped-models", "export", "import", "share", "copy", "name", "session", "changelog", "hotkeys", "fork", "clone", "tree", "login", "logout", "new", "compact", "reload", "resume", "quit"], BuiltInSlashCommandCatalog.Names.ToArray());

    [Fact]
    public void BuiltInCatalogGroupsAliasesByLogicalCommand()
    {
        Assert.Equal(19, BuiltInSlashCommandCatalog.Commands.Length);
        Assert.Contains(BuiltInSlashCommandCatalog.Commands, command => command.Names.SequenceEqual(["model", "models"]));
        Assert.Contains(BuiltInSlashCommandCatalog.Commands, command => command.Names.SequenceEqual(["resume", "session"]));
        Assert.Contains(BuiltInSlashCommandCatalog.Commands, command => command.Names.SequenceEqual(["fork", "clone"]));
    }

    [Fact]
    public async Task SharedRegistryFactoryIncludesSkillAndPromptTemplateCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-shared-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var promptPath = Path.Combine(root, "release.md");
        await File.WriteAllTextAsync(promptPath, "---\ndescription: Release prompt\n---\nRelease {{1}}");
        var runtime = await ModeTestRuntime.CreateAsync(
            args: new CliArgs(PromptTemplates: [promptPath]),
            cwd: root,
            skills: [new Skill("research", "Research skill", "content", Path.Combine(root, "skills", "research", "SKILL.md"))]);

        var registry = SlashCommandRegistryFactory.Create(runtime);

        Assert.Contains(registry.Commands, command => command.Name == "skill:research" && command.SourceId == "skill");
        Assert.Contains(registry.Commands, command => command.Name == "prompt:release" && command.SourceId == "prompt-template");
    }

    [Fact]
    public async Task SharedRegistryFactoryPreservesBuiltInPrecedenceOverExtensionCollisions()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterCommand("extension", new ExtensionCommandRegistration("model", "extension model", (_, _) => Task.CompletedTask));
        var runtime = await ModeTestRuntime.CreateAsync(extensionManager: new ExtensionManager(registry));

        var commands = SlashCommandRegistryFactory.Create(runtime);

        Assert.Contains(commands.Commands, command => command.Name == "model" && command.SourceId == "builtin");
        Assert.Contains(commands.Commands, command => command.Name == "model:1" && command.SourceId == "extension");
    }

    [Fact]
    public async Task UnknownCommandReturnsUnhandledErrorResult()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("missing", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/missing", context, CancellationToken.None);

        Assert.False(result.Handled);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ModelWithoutArgumentsInvokesSelector()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        IReadOnlyList<string>? options = null;
        var context = new SlashCommandContext(
            "model",
            runtime,
            (_, choices, _) => { options = choices; return Task.FromResult<string?>(choices[0]); },
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/model", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.NotNull(options);
        Assert.NotEmpty(options);
        Assert.Contains("Model set to", result.Message);
    }

    [Fact]
    public async Task ModelsAliasInvokesSameSelectorAsModel()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        IReadOnlyList<string>? options = null;
        var context = new SlashCommandContext(
            "models",
            runtime,
            (_, choices, _) => { options = choices; return Task.FromResult<string?>(choices[0]); },
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/models", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.NotNull(options);
        Assert.NotEmpty(options);
        Assert.Contains("Model set to", result.Message);
    }

    [Fact]
    public async Task ModelWithoutArgumentsQueriesStoredProvidersAndAwaitsThemBeforeSelector()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var storage = new BlockingOAuthStorage();
        var selectorCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new SlashCommandContext(
            "model",
            runtime,
            (_, choices, _) =>
            {
                selectorCalled.TrySetResult();
                return Task.FromResult<string?>(choices[0]);
            },
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            OAuthStorage: storage);

        var executeTask = Task.Run(async () => await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/model", context, CancellationToken.None));
        var firstCompleted = await Task.WhenAny(selectorCalled.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        storage.Complete([]);
        var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(storage.WasCalled);
        Assert.True(result.Handled);
        Assert.Contains("Model set to", result.Message);
    }

    [Fact]
    public async Task ModelWithoutArgumentsAwaitsOAuthStorageBeforeBuildingCandidates()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var storage = new InMemoryOAuthStorage();
        await storage.SetTokenAsync("anthropic", "test-key");
        IReadOnlyList<string>? options = null;
        var context = new SlashCommandContext(
            "model",
            runtime,
            (_, choices, _) => { options = choices; return Task.FromResult<string?>(choices[0]); },
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            OAuthStorage: storage);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/model", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.NotNull(options);
        Assert.NotEmpty(options);
        Assert.Contains(options, o => o.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownModelQueryDoesNotFallBackToDefaultModel()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var original = runtime.Harness.Model;
        var context = new SlashCommandContext("model", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/model definitely-not-a-real-model", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal(original, runtime.Harness.Model);
    }

    [Fact]
    public async Task ResumeWithoutArgumentsUsesSpecializedSessionSelector()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var original = runtime.Session.Metadata;
        await runtime.Session.Storage.AppendEntryAsync(UserEntry("original-message", "original"));
        await runtime.NewSessionAsync();
        var replacement = runtime.Session.Metadata;
        await runtime.Session.Storage.AppendEntryAsync(UserEntry("replacement-message", "replacement"));
        await runtime.SwitchSessionAsync(original);
        IReadOnlyList<JsonlSessionMetadata>? currentSessions = null;
        var allLoaderCalled = false;
        var genericSelectorCalled = false;
        var context = new SlashCommandContext(
            "resume",
            runtime,
            (_, _, _) => { genericSelectorCalled = true; return Task.FromResult<string?>(null); },
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            SelectSessionMetadataAsync: async (loadCurrent, loadAll, _, token) =>
            {
                currentSessions = await loadCurrent(token);
                allLoaderCalled = false;
                return replacement;
            });

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/resume", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(genericSelectorCalled);
        Assert.NotNull(currentSessions);
        Assert.False(allLoaderCalled);
        Assert.Equal(replacement.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task ResumeSelectorCancellationLeavesCurrentSessionUnchanged()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var original = runtime.Session.Metadata;
        await runtime.Session.Storage.AppendEntryAsync(UserEntry("original-message", "original"));
        await runtime.NewSessionAsync();
        await runtime.SwitchSessionAsync(original);
        var context = new SlashCommandContext(
            "resume",
            runtime,
            (_, _, _) => throw new InvalidOperationException("generic selector should not be used"),
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            SelectSessionMetadataAsync: (_, _, _, _) => Task.FromResult<JsonlSessionMetadata?>(null));
        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/resume", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("Resume cancelled.", result.Message);
        Assert.Equal(original.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task ResumeArgumentStillSwitchesWithoutSelector()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var original = runtime.Session.Metadata;
        await runtime.Session.Storage.AppendEntryAsync(UserEntry("original-message", "original"));
        await runtime.NewSessionAsync();
        var replacement = runtime.Session.Metadata;
        await runtime.Session.Storage.AppendEntryAsync(UserEntry("replacement-message", "replacement"));
        await runtime.SwitchSessionAsync(original);
        var specializedSelectorCalled = false;
        var context = new SlashCommandContext(
            "resume",
            runtime,
            (_, _, _) => throw new InvalidOperationException("generic selector should not be used"),
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            SelectSessionMetadataAsync: (_, _, _, _) =>
            {
                specializedSelectorCalled = true;
                return Task.FromResult<JsonlSessionMetadata?>(null);
            });

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync($"/resume {replacement.Id}", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(specializedSelectorCalled);
        Assert.Equal(replacement.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task SkillCommandInvokesHarnessPromptWithSkillCommandText()
    {
        var runtime = await ModeTestRuntime.CreateAsync(skills: [new Skill("example", "Example skill", "content", "/repo/skills/example/SKILL.md")]);
        string? prompted = null;
        var context = new SlashCommandContext(
            "skill:example",
            runtime,
            (_, _, _) => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult<string?>(null),
            (_, _) => Task.CompletedTask,
            SubmitPromptAsync: (text, _) => { prompted = text; return Task.CompletedTask; });
        var registry = SlashCommandRegistryFactory.Create(runtime);

        var result = await registry.ExecuteAsync("/skill:example extra input", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("/skill:example extra input", prompted);
    }

    [Fact]
    public async Task CompletionIncludesSkillCommands()
    {
        var runtime = await ModeTestRuntime.CreateAsync(skills: [new Skill("example", "Example skill", "content", "/repo/skills/example/SKILL.md")]);
        var registry = SlashCommandRegistryFactory.Create(runtime);

        Assert.Contains("/skill:example", registry.Complete("/skill:ex"));
    }

    [Fact]
    public async Task TuiHostOptionsExposeRuntimeExtensionShortcuts()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterShortcut("extension:test", new ExtensionShortcutRegistration("ctrl+k", "Run test", (_, _) => Task.CompletedTask));
        var runtime = await ModeTestRuntime.CreateAsync(extensionManager: new ExtensionManager(registry));

        var options = InteractiveMode.CreateTuiHostOptions(runtime);
        var shortcuts = options.GetExtensionShortcuts?.Invoke();

        Assert.NotNull(shortcuts);
        var shortcut = Assert.Single(shortcuts);
        Assert.Equal("extension:test", shortcut.SourceId);
        Assert.Equal("ctrl+k", shortcut.Value.Keys);
    }

    [Fact]
    public async Task TuiHostOptionsExposeRuntimeExtensionLoadStatus()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        runtime.ExtensionLoadCoordinator.MarkDiscovered("a.ts");
        runtime.ExtensionLoadCoordinator.MarkReady("b.ts");
        runtime.ExtensionLoadCoordinator.MarkFailed("c.ts", "boom");

        var options = InteractiveMode.CreateTuiHostOptions(runtime);
        var status = options.GetExtensionLoadStatus?.Invoke();

        Assert.NotNull(status);
        Assert.Equal(3, status!.Total);
        Assert.Equal(1, status.Active);
        Assert.Equal(1, status.Ready);
        Assert.Equal(1, status.Failed);
    }

    [Fact]
    public async Task TuiHostOptionsFooterSnapshotCachesGitBranchBetweenRenders()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var resolveCalls = 0;
        var footerProvider = new TuiFooterSnapshotProvider(
            resolveGitBranch: _ =>
            {
                resolveCalls++;
                return "main";
            },
            clock: () => DateTimeOffset.UnixEpoch,
            gitBranchCacheDuration: TimeSpan.FromMinutes(1));
        var options = InteractiveMode.CreateTuiHostOptions(runtime, footerProvider);
        var state = TuiRenderState.Empty("sid", "session.jsonl", new ModelDescriptor("test", "model", "test", ContextWindow: 100), ThinkingLevel.Off, null);

        var first = options.FooterSnapshot!.Invoke(state);
        var second = options.FooterSnapshot!.Invoke(state);

        Assert.Equal("main", first.GitBranch);
        Assert.Equal("main", second.GitBranch);
        Assert.Equal(1, resolveCalls);
    }

    [Fact]
    public async Task TuiHostOptionsFooterSnapshotUsesHydratedBranchWithoutReadingSessionOnEachRender()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-footer-branch-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new PiSharp.Agent.Sessions.JsonlSessionRepo(new PiSharp.Runtime.IO.SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var countingSession = new CountingSession(session);
        var runtime = new PiSharp.Runtime.SessionRuntime(
            repo,
            createOptions,
            harnessSession => ModeTestRuntime.Harness(harnessSession, ModeTestRuntime.FakeStream("ok")),
            countingSession);
        var branchEntries = new SessionTreeEntry[]
        {
            new MessageEntry
            {
                Id = "entry-1",
                ParentId = null,
                Timestamp = DateTimeOffset.UnixEpoch,
                Message = AgentMessages.User("hello")
            }
        };
        var state = TuiRenderState.Empty(
                countingSession.Metadata.Id,
                countingSession.Metadata.Path,
                new ModelDescriptor("test", "model", "test", ContextWindow: 100),
                ThinkingLevel.Off,
                sessionName: null)
            .HydrateSession(countingSession.Metadata.Id, countingSession.Metadata.Path, sessionName: null, branchEntries);
        var options = InteractiveMode.CreateTuiHostOptions(runtime);

        options.FooterSnapshot!.Invoke(state);
        options.FooterSnapshot!.Invoke(state);

        Assert.Equal(0, countingSession.GetBranchCount);
    }

    [Fact]
    public async Task TuiHostOptionsThinkingCycleLogsModelAndSupportedLevels()
    {
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var runtime = await ModeTestRuntime.CreateAsync(loggerFactory: loggerFactory);
        var harnessId = RuntimeHelpers.GetHashCode(runtime.Harness);

        var options = InteractiveMode.CreateTuiHostOptions(runtime);

        Assert.NotNull(options.CycleThinkingLevelAsync);
        await options.CycleThinkingLevelAsync!(CancellationToken.None);

        Assert.Contains(provider.Messages, message =>
            message.Contains("Thinking level cycle requested", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("model=test/test", StringComparison.Ordinal)
            && message.Contains("currentLevel=Off", StringComparison.Ordinal)
            && message.Contains("nextLevel=Off", StringComparison.Ordinal)
            && message.Contains("supportedLevels=Off", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Thinking level cycle applied", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("harnessThinking=Off", StringComparison.Ordinal)
            && message.Contains("selectionThinking=Off", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Thinking level cycle persisted", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("selectionThinking=Off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TuiHostProcessInputAsyncContinuesAfterRuntimeInputDispatchWhenCallerSynchronizationContextDoesNotPump()
    {
        var registry = new ExtensionRegistry();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterHandler("extension:test", ExtensionEventNames.Input, async (evt, _) =>
        {
            handlerStarted.TrySetResult();
            await handlerCanComplete.Task.ConfigureAwait(false);
            evt.TransformInput("transformed");
        });
        var runtime = await ModeTestRuntime.CreateAsync(extensionManager: new ExtensionManager(registry));
        var options = InteractiveMode.CreateTuiHostOptions(runtime);
        Assert.NotNull(options.ProcessInputAsync);

        var previousContext = SynchronizationContext.Current;
        Task<PiSharp.Tui.Interactive.TuiInputHookResult> inputTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            inputTask = options.ProcessInputAsync!("original", null, "interactive", CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handlerCanComplete.TrySetResult();

        var result = await inputTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Handled);
        Assert.Equal("transformed", result.Text);
    }

    [Fact]
    public void BuiltInConflictsWinOverExtensionCommands()
    {
        var registry = BuiltInSlashCommands.CreateRegistry();
        registry.Register(new SlashCommandDefinition("model", "extension model", (_, _, _) => Task.FromResult(new SlashCommandResult(true)), "extension:test"));

        Assert.Contains(registry.Commands, command => command.Name == "model" && command.SourceId == "builtin");
        Assert.Contains(registry.Commands, command => command.Name == "model:1" && command.SourceId == "extension:test");
    }

    [Fact]
    public void CompletionIncludesBuiltInsAndCollisionNames()
    {
        var registry = BuiltInSlashCommands.CreateRegistry();
        registry.Register(new SlashCommandDefinition("model", "extension model", (_, _, _) => Task.FromResult(new SlashCommandResult(true)), "extension:test"));

        Assert.Contains("/model", registry.Complete("/mo"));
        Assert.Contains("/model", registry.Complete("/mdl"));
        Assert.Contains("/scoped-models", registry.Complete("/smd"));
        Assert.Contains("/model:1", registry.Complete("/model"));
    }

    [Fact]
    public async Task NameCommandSetsSessionName()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("name", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/name test-session", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("Session name set to \"test-session\".", result.Message);
        Assert.Equal("test-session", await runtime.Session.GetSessionNameAsync());
    }

    [Fact]
    public async Task NameCommandWithEmptyArgsShowsUsage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("name", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/name  ", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Equal("Usage: /name <session-name>", result.Message);
    }

    [Fact]
    public void DuplicateExtensionCommandsUseStableInvocationSuffixes()
    {
        var registry = BuiltInSlashCommands.CreateRegistry();
        registry.Register(new SlashCommandDefinition("schedule", "first", (_, _, _) => Task.FromResult(new SlashCommandResult(true)), "extension:first"));
        registry.Register(new SlashCommandDefinition("schedule", "second", (_, _, _) => Task.FromResult(new SlashCommandResult(true)), "extension:second"));

        Assert.Contains(registry.Commands, command => command.Name == "schedule:1" && command.SourceId == "extension:first");
        Assert.Contains(registry.Commands, command => command.Name == "schedule:2" && command.SourceId == "extension:second");
        Assert.Contains("/schedule:1", registry.Complete("/schedule"));
        Assert.Contains("/schedule:2", registry.Complete("/schedule"));
    }

    [Fact]
    public async Task ReloadCommandInvokesExtensionReload()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("reload", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/reload", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal("Extensions reloaded.", result.Message);
    }

    [Fact]
    public void LoginAndLogoutAreInBuiltInNames()
    {
        Assert.Contains("login", BuiltInSlashCommands.Names);
        Assert.Contains("logout", BuiltInSlashCommands.Names);
    }

    [Fact]
    public async Task LoginCommandWithNoProviderShowsUsage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("login", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/login", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("Auth storage", result.Message);
    }

    [Fact]
    public async Task LoginCommandWithProviderReturnsInfoMessage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("login", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/login anthropic", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("Auth storage", result.Message);
    }

    [Fact]
    public async Task LoginOAuthCommandOpensAuthUrlWithoutImmediateManualPrompt()
    {
        var originalProvider = OAuthProviderRegistry.Get("openai-codex");
        OAuthProviderRegistry.Unregister("openai-codex");
        OAuthProviderRegistry.Register(new RecordingOAuthProvider("openai-codex", "https://auth.example.test/login"));

        try
        {
            var runtime = await ModeTestRuntime.CreateAsync();
            var storage = new InMemoryOAuthStorage();
            var notifications = new List<string>();
            var inputCalled = false;
            string? openedUrl = null;

            var context = new SlashCommandContext(
                "login",
                runtime,
                (_, _, _) => Task.FromResult<string?>(null),
                (_, _) =>
                {
                    inputCalled = true;
                    return Task.FromResult<string?>(null);
                },
                (message, _) =>
                {
                    notifications.Add(message);
                    return Task.CompletedTask;
                },
                OAuthStorage: storage,
                OpenUrlAsync: (url, _) =>
                {
                    openedUrl = url;
                    return Task.CompletedTask;
                });

            var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/login openai-codex", context, CancellationToken.None);

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Equal("https://auth.example.test/login", openedUrl);
            Assert.Contains("https://auth.example.test/login", notifications);
            Assert.Contains("Complete login in your browser.", notifications);
            Assert.False(inputCalled);
            Assert.NotNull(await storage.GetOAuthCredentialsAsync("openai-codex"));
        }
        finally
        {
            OAuthProviderRegistry.Unregister("openai-codex");
            if (originalProvider is not null) OAuthProviderRegistry.Register(originalProvider);
        }
    }

    [Fact]
    public async Task LogoutCommandReturnsInfoMessage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("logout", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/logout", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("Auth storage", result.Message);
    }

    [Fact]
    public void SettingsSlashCommandIsRegistered()
    {
        var registry = BuiltInSlashCommands.CreateRegistry();
        Assert.Contains(registry.Commands, cmd => cmd.Name == "settings");
    }

    [Fact]
    public async Task SettingsCommandReturnsRuntimeInfo()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("settings", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/settings", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Current settings:", result.Message);
        Assert.Contains("Provider:", result.Message);
        Assert.Contains("Model:", result.Message);
        Assert.Contains("Thinking:", result.Message);
        Assert.Contains("Session:", result.Message);
    }

    [Fact]
    public async Task ExportCommandProducesHtmlOutput()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("export", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var exportPath = Path.Combine(Path.GetTempPath(), $"pisharp-test-export-{Guid.NewGuid():N}.html");
        try
        {
            var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync($"/export {exportPath}", context, CancellationToken.None);

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains(exportPath, result.Message);
            Assert.True(File.Exists(exportPath));
        }
        finally
        {
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    [Fact]
    public async Task ExportCommandWithoutPathUsesDefault()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("export", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/export", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Exported session to", result.Message);
        Assert.Contains(Path.GetTempPath(), result.Message);
    }

    [Fact]
    public async Task ImportCommandWithoutPathShowsUsage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("import", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/import", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("Usage", result.Message);
    }

    [Fact]
    public async Task ImportCommandWithMissingFileReturnsNotFound()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("import", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/import /nonexistent/file.jsonl", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task ImportCommandWithValidFileImportsSession()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var originalSessionId = runtime.Session.Metadata.Id;
        var tempDir = Path.GetTempPath();
        var testFileId = Guid.NewGuid().ToString();
        var testFilePath = Path.Combine(tempDir, $"pisharp-test-import-{testFileId}.jsonl");
        var testCwd = Path.Combine(tempDir, $"pisharp-test-import-cwd-{testFileId}");
        Directory.CreateDirectory(testCwd);
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var escapedCwd = testCwd.Replace("\\", "\\\\");
            var headerLine = $"{{\"type\":\"session\",\"version\":3,\"id\":\"{testFileId}\",\"timestamp\":\"{timestamp:O}\",\"cwd\":\"{escapedCwd}\"}}";
            var entryLine = "{\"id\":\"1\",\"parentId\":null,\"timestamp\":" + timestamp.ToUnixTimeMilliseconds().ToString() + ",\"type\":\"message\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello import\"}],\"timestamp\":" + timestamp.ToUnixTimeMilliseconds().ToString() + "}}";
            await File.WriteAllTextAsync(testFilePath, headerLine + "\n" + entryLine + "\n");

            var context = new SlashCommandContext("import", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);
            var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync($"/import {testFilePath}", context, CancellationToken.None);

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains("Imported session", result.Message);
            Assert.NotEqual(originalSessionId, runtime.Session.Metadata.Id);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
            if (Directory.Exists(testCwd)) Directory.Delete(testCwd, recursive: true);
        }
    }

    [Fact]
    public async Task ShareCommandWithoutPathShowsUsage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("share", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/share", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("Usage", result.Message);
    }

    [Fact]
    public async Task ShareCommandCopiesSessionFile()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        await runtime.Session.AppendMessageAsync(AgentMessages.User("hello"), CancellationToken.None);
        var context = new SlashCommandContext("share", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var targetPath = Path.Combine(Path.GetTempPath(), $"pisharp-test-share-{Guid.NewGuid():N}.jsonl");
        try
        {
            var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync($"/share {targetPath}", context, CancellationToken.None);

            Assert.True(result.Handled);
            Assert.False(result.IsError);
            Assert.Contains(targetPath, result.Message);
            Assert.True(File.Exists(targetPath));
        }
        finally
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
        }
    }

    [Fact]
    public async Task CopyCommandReturnsLastAssistantText()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        await runtime.Session.AppendMessageAsync(AgentMessages.User("hello"), CancellationToken.None);
        await runtime.Session.AppendMessageAsync(AgentMessages.Assistant("world"), CancellationToken.None);
        var context = new SlashCommandContext("copy", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/copy", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("world", result.Message);
    }

    [Fact]
    public async Task CopyCommandWithNoAssistantMessageReturnsError()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("copy", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/copy", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.True(result.IsError);
        Assert.Contains("No assistant message", result.Message);
    }

    [Fact]
    public async Task ChangelogCommandReturnsInfoMessage()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("changelog", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/changelog", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("Changelog", result.Message);
    }

    [Theory]
    [InlineData("## [1.0.0]\nInitial release\n## [0.9.0]\nBeta", 2, "1.0.0", "Initial release")]
    [InlineData("## [2.0.0]\n### Added\n- Feature X\n### Fixed\n- Bug Y", 1, "2.0.0", "### Added")]
    [InlineData("No version headers here\nJust text", 0, null, null)]
    [InlineData("## 1.0.0\nNo brackets\n## 2.0.0\nAlso no brackets", 2, "1.0.0", "No brackets")]
    public void ChangelogParserParsesVersionHeaders(string markdown, int expectedCount, string? firstVersion, string? containsContent)
    {
        var entries = ChangelogParser.Parse(markdown);

        Assert.Equal(expectedCount, entries.Count);
        if (firstVersion is not null)
        {
            var entry = entries[0];
            Assert.Equal(firstVersion, $"{entry.Major}.{entry.Minor}.{entry.Patch}");
            if (containsContent is not null)
                Assert.Contains(containsContent, entry.Content);
        }
    }

    [Fact]
    public void ChangelogParserFindPathReturnsNullForMissingFile()
    {
        var path = ChangelogParser.FindChangelogPath();
        // No CHANGELOG.md exists in this repo; may return null
        Assert.True(path is null || File.Exists(path));
    }

    [Fact]
    public async Task ScopedModelsCommandWithNoScopedModelsShowsHint()
    {
        var runtime = await ModeTestRuntime.CreateAsync();
        var context = new SlashCommandContext("scoped-models", runtime, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);

        var result = await BuiltInSlashCommands.CreateRegistry().ExecuteAsync("/scoped-models", context, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.False(result.IsError);
        Assert.Contains("No scoped models", result.Message);
    }

    private static MessageEntry UserEntry(string id, string text)
        => new() { Id = id, ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User(text) };

    private sealed class CountingSession(ISession<JsonlSessionMetadata> inner) : ISession<JsonlSessionMetadata>
    {
        public int GetBranchCount { get; private set; }
        public JsonlSessionMetadata Metadata => inner.Metadata;
        public string Id => inner.Id;
        public string? LeafId { get => inner.LeafId; set => inner.LeafId = value; }
        public ISessionStorage<JsonlSessionMetadata> Storage => inner.Storage;
        public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => inner.GetLeafIdAsync(cancellationToken);
        public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => inner.GetEntryAsync(id, cancellationToken);
        public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => inner.GetEntriesAsync(cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetBranchAsync(string? fromId = null, CancellationToken cancellationToken = default)
        {
            GetBranchCount++;
            return inner.GetBranchAsync(fromId, cancellationToken);
        }

        public Task<SessionContext> BuildContextAsync(CancellationToken cancellationToken = default) => inner.BuildContextAsync(cancellationToken);
        public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => inner.GetLabelAsync(id, cancellationToken);
        public Task<string?> GetSessionNameAsync(CancellationToken cancellationToken = default) => inner.GetSessionNameAsync(cancellationToken);
        public Task<string> AppendMessageAsync(AgentMessage message, CancellationToken cancellationToken = default) => inner.AppendMessageAsync(message, cancellationToken);
        public Task<IReadOnlyList<string>> AppendEntriesAsync(IReadOnlyList<SessionTreeEntry> entries, CancellationToken cancellationToken = default) => inner.AppendEntriesAsync(entries, cancellationToken);
        public Task<string> AppendThinkingLevelChangeAsync(string thinkingLevel, CancellationToken cancellationToken = default) => inner.AppendThinkingLevelChangeAsync(thinkingLevel, cancellationToken);
        public Task<string> AppendModelChangeAsync(string provider, string modelId, CancellationToken cancellationToken = default) => inner.AppendModelChangeAsync(provider, modelId, cancellationToken);
        public Task<string> AppendCompactionAsync(string summary, string firstKeptEntryId, int tokensBefore, object? details = null, bool? fromHook = null, CancellationToken cancellationToken = default) => inner.AppendCompactionAsync(summary, firstKeptEntryId, tokensBefore, details, fromHook, cancellationToken);
        public Task<string> AppendCustomEntryAsync(string customType, object? data = null, CancellationToken cancellationToken = default) => inner.AppendCustomEntryAsync(customType, data, cancellationToken);
        public Task<string> AppendCustomMessageEntryAsync(string customType, object content, bool display, object? details = null, CancellationToken cancellationToken = default) => inner.AppendCustomMessageEntryAsync(customType, content, display, details, cancellationToken);
        public Task<string> AppendLabelAsync(string targetId, string? label, CancellationToken cancellationToken = default) => inner.AppendLabelAsync(targetId, label, cancellationToken);
        public Task<string> AppendSessionNameAsync(string name, CancellationToken cancellationToken = default) => inner.AppendSessionNameAsync(name, cancellationToken);
        public Task<string?> MoveToAsync(string? entryId, BranchSummaryEntry? summary = null, CancellationToken cancellationToken = default) => inner.MoveToAsync(entryId, summary, cancellationToken);
    }

    private sealed class BlockingOAuthStorage : IOAuthStorage
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _storedProviders = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetTokenAsync(string provider, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task SetTokenAsync(string provider, string token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveTokenAsync(string provider, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetOAuthCredentialsAsync(string provider, OAuthCredentials credentials, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<OAuthCredentials?> GetOAuthCredentialsAsync(string provider, CancellationToken cancellationToken = default)
            => Task.FromResult<OAuthCredentials?>(null);

        public Task<IReadOnlyList<string>> ListStoredProvidersAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return _storedProviders.Task;
        }

        public bool WasCalled { get; private set; }

        public void Complete(IReadOnlyList<string> providers)
            => _storedProviders.TrySetResult(providers);
    }

    private sealed class InMemoryOAuthStorage : IOAuthStorage
    {
        private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OAuthCredentials> _oauthCredentials = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetTokenAsync(string provider, CancellationToken cancellationToken = default)
            => Task.FromResult(_tokens.TryGetValue(provider, out var token) ? token : null);

        public Task SetTokenAsync(string provider, string token, CancellationToken cancellationToken = default)
        {
            _tokens[provider] = token;
            return Task.CompletedTask;
        }

        public Task RemoveTokenAsync(string provider, CancellationToken cancellationToken = default)
        {
            _tokens.Remove(provider);
            return Task.CompletedTask;
        }

        public Task SetOAuthCredentialsAsync(string provider, OAuthCredentials credentials, CancellationToken cancellationToken = default)
        {
            _oauthCredentials[provider] = credentials;
            return Task.CompletedTask;
        }

        public Task<OAuthCredentials?> GetOAuthCredentialsAsync(string provider, CancellationToken cancellationToken = default)
            => Task.FromResult(_oauthCredentials.TryGetValue(provider, out var credentials) ? credentials : null);

        public Task<IReadOnlyList<string>> ListStoredProvidersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_tokens.Keys.Concat(_oauthCredentials.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed class RecordingOAuthProvider(string id, string authUrl) : IOAuthProvider
    {
        public string Id => id;
        public string Name => "Recording OAuth";
        public bool UsesCallbackServer => true;

        public async Task<OAuthCredentials> LoginAsync(OAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default)
        {
            if (callbacks.OnManualCodeInput is not null)
                throw new InvalidOperationException("Manual code input should not be started immediately by slash-command login.");

            await callbacks.OnAuth(new OAuthAuthInfo(authUrl, "Complete login in your browser."));
            return new OAuthCredentials("refresh", "access", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3600000);
        }

        public Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);

        public string GetApiKey(OAuthCredentials credentials) => credentials.Access;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName)
            => new RecordingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel)
                => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));

            private sealed class NullScope : IDisposable
            {
                public static NullScope Instance { get; } = new();

                public void Dispose()
                {
                }
            }
        }
    }

}

file sealed class NonPumpingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
    }
}
