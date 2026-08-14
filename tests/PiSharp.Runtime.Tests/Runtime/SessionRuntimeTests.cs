using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Serialization;
using PiSharp.Agent.Sessions;
using PiSharp.Compatibility.Resources;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.TsBridge;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

public sealed class SessionRuntimeTests
{
    [Fact]
    public async Task RuntimeExposesLoadedSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var skill = new Skill("example", "Example skill", "Use example", "/repo/skills/example/SKILL.md");
        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial, skills: [skill]);

        Assert.Equal(["example"], runtime.Skills.Select(item => item.Name));
    }

    [Fact]
    public async Task NewSessionRecreatesHarnessAndInvokesRebindCallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var oldHarness = runtime.Harness;
        var rebound = false;
        runtime.SetRebindSession((_, _) => { rebound = true; return Task.CompletedTask; });

        await runtime.NewSessionAsync();

        Assert.True(rebound);
        Assert.NotSame(oldHarness, runtime.Harness);
        Assert.NotEqual(initial.Metadata.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task SwitchSessionRecreatesHarnessAndPreservesModelState()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var target = await repo.CreateAsync(createOptions with { Id = "target" });
        await target.AppendMessageAsync(AgentMessages.User("target"));
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        var oldHarness = runtime.Harness;
        await runtime.Harness.SetModelAsync(new ModelDescriptor("provider", "model", "label"), "test");

        await runtime.SwitchSessionAsync(target.Metadata);

        Assert.NotSame(oldHarness, runtime.Harness);
        Assert.Equal(target.Metadata.Id, runtime.Session.Metadata.Id);
        Assert.Equal("model", runtime.Harness.Model.Id);
    }

    [Fact]
    public async Task ForkSessionReplacesCurrentSessionWithFork()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);

        await runtime.ForkAsync(initial.Metadata, new SessionForkOptions(Id: "forked"));

        Assert.Equal("forked", runtime.Session.Metadata.Id);
        Assert.NotEqual(initial.Metadata.Path, runtime.Session.Metadata.Path);
    }

    [Fact]
    public async Task BindExtensionRuntimeReplaysExistingRegistryToolsToHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        registry.RegisterTool("extension:test", new RuntimeTestTool("extension_search"));
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();

        Assert.Contains("extension_search", runtime.Harness.AllToolNames);
    }

    [Fact]
    public async Task SubagentPromptSnapshotExposesCompletedMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);
        runtime.BindExtensionRuntime();
        var created = await runtime.ExtensionBinding.CreateAgentSessionAsync(null, CancellationToken.None);
        using var createdDocument = JsonDocument.Parse(AgentJsonSerializer.Serialize(created));
        var sessionId = createdDocument.RootElement.GetProperty("sessionId").GetString()!;

        var prompted = await runtime.ExtensionBinding.AgentSessionPromptAsync(sessionId, "hello", null, CancellationToken.None);

        using var promptedDocument = JsonDocument.Parse(AgentJsonSerializer.Serialize(prompted));
        var messages = promptedDocument.RootElement.GetProperty("session").GetProperty("messages");
        var assistant = Assert.Single(messages.EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant");
        var content = assistant.GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains(content, part => part.GetProperty("type").GetString() == "text" && part.GetProperty("text").GetString() == "ok");
    }

    [Fact]
    public async Task BindExtensionRuntimeAppliesRegistryToolChangesToHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();
        var handle = registry.RegisterTool("extension:test", new RuntimeTestTool("extension_dynamic"));

        Assert.Contains("extension_dynamic", runtime.Harness.AllToolNames);
        handle.Dispose();
        Assert.DoesNotContain("extension_dynamic", runtime.Harness.AllToolNames);
    }

    [Fact]
    public async Task SendExtensionMessageRejectsToolResultMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SendExtensionMessageAsync(
            AgentMessages.ToolResult("call-orphan", "read", "orphan result"),
            ExtensionMessageDelivery.NextTurn,
            triggerTurn: false,
            CancellationToken.None));

        Assert.Contains("ToolResultMessage", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await initial.GetEntriesAsync());
    }

    [Fact]
    public async Task DisposeAsyncIgnoresSessionShutdownExtensionPipeFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.SessionShutdown, (_, _) => throw new IOException("The pipe is being closed."));
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        var exception = await Record.ExceptionAsync(() => runtime.DisposeAsync().AsTask());

        Assert.Null(exception);
    }

    [Fact]
    public async Task HarnessEventForwardingDoesNotWaitForOrdinaryTypeScriptSubscribers()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-forwarding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var extensionPath = Path.Combine(root, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on('model_select', async () => {
                await new Promise(resolve => setTimeout(resolve, 1000));
                pi.registerCommand('slow-model-select-finished', { description: 'Finished slow model_select', handler: () => 'ok' });
              });
            }
            """);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var tsHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: root), registry);
        await tsHost.StartAsync(CancellationToken.None);
        await using var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: new ExtensionManager(registry), tsHost: tsHost);
        runtime.BindHarnessEventForwarding();

        var elapsed = Stopwatch.StartNew();
        await runtime.SetModelAsync(new ModelDescriptor("provider", "model", "Model"), "test", CancellationToken.None);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(500), $"Harness forwarding waited {elapsed.Elapsed.TotalMilliseconds}ms for an ordinary TypeScript event subscriber.");
        Assert.DoesNotContain(registry.Commands, command => command.Value.Name == "slow-model-select-finished");
        await WaitForCommandAsync(registry, "slow-model-select-finished", TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task BindExtensionRuntimeReplaysExistingRegistrySkillsToHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("extension_dynamic", "Dynamic skill", "body", "/repo/dynamic/SKILL.md"));
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();

        Assert.Contains("extension_dynamic", runtime.Harness.Skills.Select(skill => skill.Name));
    }

    [Fact]
    public async Task SessionSnapshotReusesUnchangedSessionEntriesAndInvalidatesAfterAppend()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-snapshot-cache-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        await initial.AppendMessageAsync(AgentMessages.User("hello"));
        var session = new CountingSession(initial);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session);

        runtime.BindExtensionRuntime();

        _ = await runtime.ExtensionBinding.GetSessionSnapshotAsync(CancellationToken.None);
        _ = await runtime.ExtensionBinding.GetSessionSnapshotAsync(CancellationToken.None);
        await session.AppendMessageAsync(AgentMessages.Assistant("updated"));
        _ = await runtime.ExtensionBinding.GetSessionSnapshotAsync(CancellationToken.None);

        Assert.Equal(2, session.GetEntriesCount);
    }

    [Fact]
    public async Task BindExtensionRuntimeAppliesRegistrySkillChangesToHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();
        var handle = registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("extension_dynamic", "Dynamic skill", "body", "/repo/dynamic/SKILL.md"));

        Assert.Contains("extension_dynamic", runtime.Harness.Skills.Select(skill => skill.Name));
        handle.Dispose();
        Assert.DoesNotContain("extension_dynamic", runtime.Harness.Skills.Select(skill => skill.Name));
    }

    [Fact]
    public async Task NewSessionReplaysExtensionSkillsToReplacementHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("extension_dynamic", "Dynamic skill", "body", "/repo/dynamic/SKILL.md"));
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();
        await runtime.NewSessionAsync();

        Assert.Contains("extension_dynamic", runtime.Harness.Skills.Select(skill => skill.Name));
    }

    [Fact]
    public async Task NewSessionPreservesDefaultSelectedSkillsForFutureExtensionSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();
        await runtime.NewSessionAsync();
        registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("late_dynamic", "Late dynamic skill", "body", "/repo/late/SKILL.md"));

        Assert.Null(runtime.Harness.ExplicitSelectedSkillNames);
        Assert.Contains("late_dynamic", runtime.Harness.SelectedSkillNames);
    }

    [Fact]
    public async Task ExtensionBindingCanSetSelectedSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var skill = new Skill("example", "Example", "body", "/repo/example/SKILL.md");
        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial, skills: [skill]);

        runtime.BindExtensionRuntime();
        await runtime.ExtensionBinding.SetSelectedSkillsAsync(["example"], CancellationToken.None);

        Assert.Equal(["example"], runtime.Harness.SelectedSkillNames);
    }

    [Fact]
    public async Task UnregisteringExtensionSkillRestoresStartupSkillWithSameName()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var baseSkill = new Skill("same", "Base skill", "base", "/repo/base/SKILL.md");
        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [baseSkill]));
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial, extensionManager: manager, skills: [baseSkill]);

        runtime.BindExtensionRuntime();
        var handle = registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("same", "Override skill", "override", "/repo/override/SKILL.md", Override: ExtensionOverridePolicy.Override));
        Assert.Equal("Override skill", runtime.Harness.Skills.Single(skill => skill.Name == "same").Description);

        handle.Dispose();

        Assert.Equal("Base skill", runtime.Harness.Skills.Single(skill => skill.Name == "same").Description);
    }

    [Fact]
    public async Task SetThinkingLevelAsyncUpdatesHarnessAndCurrentSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, ReasoningHarness, session);

        await runtime.SetThinkingLevelAsync(ThinkingLevel.High);

        Assert.Equal(ThinkingLevel.High, runtime.Harness.ThinkingLevel);
        Assert.Equal(ThinkingLevel.High, runtime.CurrentModelSelection.ThinkingLevel);
    }

    [Fact]
    public async Task SetThinkingLevelAsyncLogsRuntimeAndControllerTransitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = new RecordingLoggerFactory(provider);
        var runtime = new SessionRuntime(
            repo,
            createOptions,
            sessionMetadata => new AgentHarness<JsonlSessionMetadata>(
                new AgentHarnessOptions<JsonlSessionMetadata>(sessionMetadata, ReasoningModel, FakeStream, FakeCompletion, []),
                loggerFactory),
            session,
            loggerFactory: loggerFactory);
        var harnessId = RuntimeHelpers.GetHashCode(runtime.Harness);

        await runtime.SetThinkingLevelAsync(ThinkingLevel.High);

        Assert.Contains(provider.Messages, message =>
            message.Contains("Runtime thinking level update requested", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("currentSelectionThinking=Off", StringComparison.Ordinal)
            && message.Contains("requestedLevel=High", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Runtime model controller applying thinking level", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("requestedLevel=High", StringComparison.Ordinal)
            && message.Contains("clampedLevel=High", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Runtime model controller updated thinking level", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("harnessThinking=High", StringComparison.Ordinal)
            && message.Contains("pendingPersistenceThinking=High", StringComparison.Ordinal));
        Assert.Contains(provider.Messages, message =>
            message.Contains("Runtime thinking level update completed", StringComparison.Ordinal)
            && message.Contains($"harnessId={harnessId}", StringComparison.Ordinal)
            && message.Contains("nextSelectionThinking=High", StringComparison.Ordinal)
            && message.Contains("harnessThinking=High", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetModelAsyncPersistsDefaultsToWinningSettingsLayers()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        Directory.CreateDirectory(Path.Combine(repoRoot, ".pi", "PiSharp"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"), "{\"defaultProvider\":\"home-provider\"}\n");
        await File.WriteAllTextAsync(Path.Combine(repoRoot, ".pi", "PiSharp", "settings.json"), "{\"defaultModel\":\"project-model\"}\n");
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repoRoot, home);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), "sessions");
        var createOptions = new JsonlSessionCreateOptions(repoRoot);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, settingsStore: store, settingsSnapshot: snapshot);

        await runtime.SetModelAsync(new ModelDescriptor("new-provider", "new-model", "test"));
        await runtime.PersistCurrentModelSelectionAsync();

        var globalPiSharp = await File.ReadAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"));
        var projectPiSharp = await File.ReadAllTextAsync(Path.Combine(repoRoot, ".pi", "PiSharp", "settings.json"));
        Assert.Contains("\"defaultProvider\": \"new-provider\"", globalPiSharp);
        Assert.Contains("\"defaultModel\": \"new-model\"", projectPiSharp);
    }

    [Fact]
    public async Task SetModelAsyncDoesNotPersistSettingsImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repoRoot, home);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), "sessions");
        var createOptions = new JsonlSessionCreateOptions(repoRoot);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, settingsStore: store, settingsSnapshot: snapshot);

        await runtime.SetModelAsync(new ModelDescriptor("new-provider", "new-model", "test"));

        Assert.False(File.Exists(Path.Combine(home, ".pi", "agent", "settings.json")));
        Assert.False(File.Exists(Path.Combine(home, ".pi", "PiSharp", "settings.json")));
        Assert.False(File.Exists(Path.Combine(repoRoot, ".pi", "agent", "settings.json")));
        Assert.False(File.Exists(Path.Combine(repoRoot, ".pi", "PiSharp", "settings.json")));
    }

    [Fact]
    public async Task SetModelAsyncFallsBackToLegacyGlobalWhenNoLayerWonDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repoRoot, home);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), "sessions");
        var createOptions = new JsonlSessionCreateOptions(repoRoot);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, settingsStore: store, settingsSnapshot: snapshot);

        await runtime.SetModelAsync(new ModelDescriptor("fallback-provider", "fallback-model", "test"));
        await runtime.PersistCurrentModelSelectionAsync();

        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        Assert.True(File.Exists(settingsPath), "Disposing the runtime should persist the pending thinking level even if the session append is still blocked.");
        var globalLegacy = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains("\"defaultProvider\": \"fallback-provider\"", globalLegacy);
        Assert.Contains("\"defaultModel\": \"fallback-model\"", globalLegacy);
    }

    [Fact]
    public async Task SetThinkingLevelAsyncPersistsDefaultThinkingToSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repoRoot, home);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), "sessions");
        var createOptions = new JsonlSessionCreateOptions(repoRoot);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, ReasoningHarness, session, settingsStore: store, settingsSnapshot: snapshot);

        await runtime.SetThinkingLevelAsync(ThinkingLevel.Medium);
        await runtime.PersistCurrentModelSelectionAsync();

        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        Assert.True(File.Exists(settingsPath), "Disposing the runtime should persist the pending thinking level even if the session append is still blocked.");
        var globalLegacy = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains("\"defaultThinking\": \"medium\"", globalLegacy);
    }

    [Fact]
    public async Task DisposePersistsPendingThinkingLevelWhenSessionAppendHasNotCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repoRoot, home);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), "sessions");
        var createOptions = new JsonlSessionCreateOptions(repoRoot);
        var storage = new BlockingAppendStorage<JsonlSessionMetadata>(new JsonlSessionMetadata("sid", DateTimeOffset.UtcNow, repoRoot, Path.Combine(root, "session.jsonl")));
        var session = new Session<JsonlSessionMetadata>(storage);
        var runtime = new SessionRuntime(repo, createOptions, ReasoningHarness, session, settingsStore: store, settingsSnapshot: snapshot);

        var setTask = runtime.SetThinkingLevelAsync(ThinkingLevel.Medium);
        await storage.AppendStarted.WaitAsync(TimeSpan.FromSeconds(1));

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        var settingsPath = Path.Combine(home, ".pi", "agent", "settings.json");
        Assert.True(File.Exists(settingsPath), "Disposing the runtime should persist the pending thinking level even if the session append is still blocked.");
        var globalLegacy = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains("\"defaultThinking\": \"medium\"", globalLegacy);

        storage.ReleaseAppend();
        await setTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetThinkingLevelAsyncClampsUnsupportedThinkingBeforePersisting()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-settings-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var store = new PiSettingsStore();
        var snapshot = await store.LoadAsync(repoRoot, home);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), "sessions");
        var createOptions = new JsonlSessionCreateOptions(repoRoot);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, settingsStore: store, settingsSnapshot: snapshot);

        await runtime.SetThinkingLevelAsync(ThinkingLevel.High);
        await runtime.PersistCurrentModelSelectionAsync();

        Assert.Equal(ThinkingLevel.Off, runtime.Harness.ThinkingLevel);
        Assert.Equal(ThinkingLevel.Off, runtime.CurrentModelSelection.ThinkingLevel);
        var globalLegacy = await File.ReadAllTextAsync(Path.Combine(home, ".pi", "agent", "settings.json"));
        Assert.Contains("\"defaultThinking\": \"off\"", globalLegacy);
    }

    [Fact]
    public async Task GetExtensionLoadSummaryAggregatesCoordinatorStates()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var coordinator = new ExtensionLoadCoordinator();
        coordinator.MarkDiscovered("a.ts");
        coordinator.MarkDescriptorReplayed("b.ts");
        coordinator.MarkBackgroundLoading("background.ts");
        _ = coordinator.RunOnceAsync("c.ts", _ => Task.FromResult(new PiSharp.TsBridge.Protocol.TsExtensionLoadResult(true, "c.ts")));
        _ = coordinator.RunOnceAsync("d.ts", _ => Task.FromResult(new PiSharp.TsBridge.Protocol.TsExtensionLoadResult(false, "d.ts", "boom")));
        await coordinator.ExtensionsReadyTask;

        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionLoadCoordinator: coordinator);

        var summary = runtime.GetExtensionLoadSummary();

        Assert.Equal(5, summary.Total);
        Assert.Equal(2, summary.Active);
        Assert.Equal(1, summary.BlockingActive);
        Assert.Equal(1, summary.Ready);
        Assert.Equal(1, summary.Failed);
        Assert.Contains(summary.FailedDiagnostics, item => item.Path == "d.ts" && item.Diagnostic == "boom");
    }

    [Fact]
    public async Task TypeScriptBeforeAgentStartMutationAppliesToCurrentRuntimePrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-ts-before-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var extensionPath = Path.Combine(root, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on('before_agent_start', event => ({ systemPrompt: event.systemPrompt.replace('VISIBLE', 'FILTERED') }));
            }
            """);
        AgentContext? observed = null;
        AgentStreamAsync stream = (model, context, options, token) =>
        {
            observed = context;
            return FakeStream(model, context, options, token);
        };
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        AgentHarness<JsonlSessionMetadata> HarnessWithPrompt(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(
                session,
                new ModelDescriptor("test", "test", "test"),
                stream,
                FakeCompletion,
                [],
                SystemPrompt: new SystemPromptBuildOptions(Cwd: root, CustomPrompt: "VISIBLE PROMPT"),
                Extensions: registry));
        var manager = new ExtensionManager(registry);
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var tsHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: root), registry, binding);
        await tsHost.StartAsync(CancellationToken.None);
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithPrompt, initial, extensionManager: manager, tsHost: tsHost, extensionBinding: binding);

        runtime.BindExtensionRuntime();
        runtime.BindHarnessEventForwarding();
        await runtime.Harness.PromptAsync("hello");

        Assert.NotNull(observed);
        Assert.Contains("FILTERED PROMPT", observed!.SystemPrompt);
        Assert.DoesNotContain("VISIBLE PROMPT", observed.SystemPrompt);
    }

    [Fact]
    public async Task SubmitPromptAsyncAppliesNativeInputTransformBeforePrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var observed = new List<string>();
        AgentStreamAsync stream = (model, context, options, token) => { observed.Add(context.Messages.OfType<UserMessage>().Last().Content.OfType<TextContent>().Single().Text); return FakeStream(model, context, options, token); };
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        AgentHarness<JsonlSessionMetadata> HarnessWithRegistry(ISession<JsonlSessionMetadata> session) => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, [], Extensions: registry));
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithRegistry, initial, extensionManager: manager);
        registry.RegisterHandler("extension:test", ExtensionEventNames.Input, (evt, _) => { evt.TransformInput("transformed"); return Task.CompletedTask; });
        await runtime.SubmitPromptAsync("original", null, "rpc", CancellationToken.None);
        Assert.Equal(["transformed"], observed);
    }

    [Fact]
    public async Task SubmitPromptAsyncDoesNotPromptWhenInputHandled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var called = false;
        AgentStreamAsync stream = (model, context, options, token) => { called = true; return FakeStream(model, context, options, token); };
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        AgentHarness<JsonlSessionMetadata> HarnessWithRegistry(ISession<JsonlSessionMetadata> session) => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, [], Extensions: registry));
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithRegistry, initial, extensionManager: manager);

        registry.RegisterHandler("extension:test", ExtensionEventNames.Input, (evt, _) => { evt.HandleInput(); return Task.CompletedTask; });
        var result = await runtime.SubmitPromptAsync("/handled", null, "rpc", CancellationToken.None);
        Assert.Null(result);
        Assert.False(called);
    }

    [Fact]
    public async Task DispatchInputAsyncContinuesAfterAsyncHandlerWhenCallerSynchronizationContextDoesNotPump()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterHandler("extension:test", ExtensionEventNames.Input, async (evt, _) =>
        {
            handlerStarted.TrySetResult();
            await handlerCanComplete.Task.ConfigureAwait(false);
            evt.TransformInput("transformed");
        });
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: new ExtensionManager(registry));
        var previousContext = SynchronizationContext.Current;
        Task<SessionRuntime.RuntimeInputHookResult> dispatchTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            dispatchTask = runtime.DispatchInputAsync("original", null, "interactive", CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        handlerCanComplete.TrySetResult();

        var result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Handled);
        Assert.Equal("transformed", result.Text);
    }

    [Fact]
    public async Task SwitchSessionAsyncCanBeCancelledByNativeHook()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var target = await repo.CreateAsync(createOptions with { Id = "blocked" });
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.SessionBeforeSwitch, (evt, _) => { evt.CancelSessionChange("blocked switch"); return Task.CompletedTask; });
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: new ExtensionManager(registry));

        var result = await runtime.SwitchSessionAsync(target.Metadata);

        Assert.True(result.Cancelled);
        Assert.Equal("blocked switch", result.Reason);
        Assert.Equal(initial.Metadata.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task TypeScriptSessionBeforeSwitchCanCancelRuntimeSwitch()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-ts-session-before-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var extensionPath = Path.Combine(root, "extension.mjs");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate(pi) {
              pi.on('session_before_switch', event => event.targetSessionFile?.includes('blocked') ? { cancel: true, reason: 'blocked by ts' } : undefined);
            }
            """);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var target = await repo.CreateAsync(createOptions with { Id = "blocked" });
        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);
        var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
        await using var tsHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [extensionPath], WorkingDirectory: root), registry, binding);
        await tsHost.StartAsync(CancellationToken.None);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager, tsHost: tsHost, extensionBinding: binding);
        runtime.BindHarnessEventForwarding();

        var result = await runtime.SwitchSessionAsync(target.Metadata);

        Assert.True(result.Cancelled);
        Assert.Equal("blocked by ts", result.Reason);
        Assert.Equal(initial.Metadata.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task SwitchSessionAsyncDispatchesShutdownBeforeReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var target = await repo.CreateAsync(createOptions with { Id = "target" });
        await target.AppendMessageAsync(AgentMessages.User("target"));
        var observed = new List<ExtensionSessionShutdownEvent>();
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.SessionShutdown, (evt, _) => { observed.Add(Assert.IsType<ExtensionSessionShutdownEvent>(evt.Payload)); return Task.CompletedTask; });
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: new ExtensionManager(registry));

        var result = await runtime.SwitchSessionAsync(target.Metadata);

        Assert.False(result.Cancelled);
        Assert.Equal(target.Metadata.Id, result.Session!.Id);
        var shutdown = Assert.Single(observed);
        Assert.Equal("switch", shutdown.Reason);
        Assert.Equal(target.Metadata.Path, shutdown.TargetSessionFile);
        Assert.Equal(initial.Metadata.Id, Assert.IsType<JsonlSessionMetadata>(shutdown.Session).Id);
    }

    [Fact]
    public async Task ForkAsyncCanBeCancelledByNativeHook()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-runtime-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.SessionBeforeFork, (evt, _) => { evt.CancelSessionChange("blocked fork"); return Task.CompletedTask; });
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: new ExtensionManager(registry));

        var result = await runtime.ForkAsync(initial.Metadata, new SessionForkOptions(Id: "forked"));

        Assert.True(result.Cancelled);
        Assert.Equal("blocked fork", result.Reason);
        Assert.Equal(initial.Metadata.Id, runtime.Session.Metadata.Id);
    }

    [Fact]
    public async Task TypeScriptEmbeddingServiceAndSelectorFilterCurrentTurnSkillsEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-skill-relevance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repositoryRoot = FindRepositoryRoot();
        var embeddingsExtensionPath = Path.Combine(repositoryRoot, "extensions", "pisharp-embeddings");
        var selectorExtensionPath = Path.Combine(repositoryRoot, "extensions", "relevance-filtered-skills");
        var fakeProviderPath = Path.Combine(root, "fake-provider.mjs");
        await File.WriteAllTextAsync(fakeProviderPath, """
            export default async function activate(pi) {
              const embeddings = await pi.extensions.waitFor('pisharp.embeddings', { timeoutMs: 1000 });
              embeddings.registerProvider({
                id: 'fake',
                async embed({ input }) { return { embedding: input.includes('alpha') || input.includes('alpha task') ? [1, 0] : [0, 1], embeddings: [[1, 0]], providerId: 'fake', model: 'fake', dimensions: 2 }; },
                async embedMany({ inputs }) { return { embeddings: inputs.map(input => input.includes('alpha') ? [1, 0] : [0, 1]), providerId: 'fake', model: 'fake', dimensions: 2 }; }
              });
            }
            """);
        var previousProvider = Environment.GetEnvironmentVariable("PISHARP_EMBEDDINGS_PROVIDER");
        var previousMaxSkills = Environment.GetEnvironmentVariable("PISHARP_SKILL_RELEVANCE_MAX_SKILLS");
        Environment.SetEnvironmentVariable("PISHARP_EMBEDDINGS_PROVIDER", "fake");
        Environment.SetEnvironmentVariable("PISHARP_SKILL_RELEVANCE_MAX_SKILLS", "1");
        try
        {
            AgentContext? observed = null;
            AgentStreamAsync stream = (model, context, options, token) => { observed = context; return FakeStream(model, context, options, token); };
            var skills = new[]
            {
                new Skill("alpha", "Handles alpha task", "body", "/repo/alpha/SKILL.md"),
                new Skill("beta", "Handles beta task", "body", "/repo/beta/SKILL.md")
            };
            var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
            var createOptions = new JsonlSessionCreateOptions(root);
            var initial = await repo.CreateAsync(createOptions);
            var registry = new ExtensionRegistry();
            AgentHarness<JsonlSessionMetadata> HarnessWithSkills(ISession<JsonlSessionMetadata> session)
                => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), stream, FakeCompletion, [new RuntimeTestTool("read")], Skills: skills, Extensions: registry));
            var manager = new ExtensionManager(registry);
            var binding = new ExtensionRuntimeBinding(root, false, NoExtensionUi.Instance);
            // These extensions use real TypeScript syntax, so the bridge needs the TypeScript
            // compiler. The output-copied bridge (bin/Node) has no adjacent node_modules; run the
            // repo bridge instead, where `npm ci` (BuildTypeScriptBridgeInstall) installs the
            // `typescript` package next to the runner.
            var bridgeRunner = Path.Combine(repositoryRoot, "src", "PiSharp.TsBridge", "Node", "TsBridgeRunner.mjs");
            await using var tsHost = new TsExtensionHost(new TsBridgeOptions(ExtensionPaths: [], WorkingDirectory: root, RunnerPath: bridgeRunner), registry, binding);
            var loaded = await tsHost.LoadManyAsync([embeddingsExtensionPath, fakeProviderPath, selectorExtensionPath], binding, CancellationToken.None);
            Assert.All(loaded.Results!, item => Assert.True(item.Ok, item.Error));
            var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkills, initial, extensionManager: manager, tsHost: tsHost, extensionBinding: binding, skills: skills);

            runtime.BindExtensionRuntime();
            runtime.BindHarnessEventForwarding();
            await runtime.Harness.PromptAsync("alpha task");

            Assert.NotNull(observed);
            Assert.Contains("<name>alpha</name>", observed!.SystemPrompt);
            Assert.DoesNotContain("<name>beta</name>", observed.SystemPrompt);
            Assert.Null(runtime.Harness.ExplicitSelectedSkillNames);
            Assert.Equal(["alpha", "beta"], runtime.Harness.SelectedSkillNames);

            Assert.NotNull(runtime.Harness.LastPromptDocument);
            var promptDocument = runtime.Harness.LastPromptDocument!;
            var skillsSection = Assert.Single(promptDocument.Sections, section => section.Id == "skills.available");
            var sectionText = Assert.IsType<RawPromptContent>(skillsSection.Content).Text;
            Assert.Contains("<name>alpha</name>", sectionText);
            Assert.DoesNotContain("<name>beta</name>", sectionText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_EMBEDDINGS_PROVIDER", previousProvider);
            Environment.SetEnvironmentVariable("PISHARP_SKILL_RELEVANCE_MAX_SKILLS", previousMaxSkills);
        }
    }

    [Fact]
    public async Task BindExtensionRuntimeExposesJavaScriptCompatibleCommandInfo()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-commands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skillPath = Path.Combine(root, "skills", "research", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        await File.WriteAllTextAsync(skillPath, "skill content");
        var promptPath = Path.Combine(root, "prompts", "release.md");
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        await File.WriteAllTextAsync(promptPath, "prompt content");

        var extensionManager = new ExtensionManager();
        extensionManager.Registry.RegisterCommand(
            "extension:sample",
            new ExtensionCommandRegistration("sample", "Sample command", (_, _) => Task.CompletedTask));
        var promptTemplates = new PromptTemplateCatalog();
        promptTemplates.Add(new PromptTemplateRegistration("release", "Release prompt", "body", promptPath));
        var skill = new Skill("research", "Research skill", "content", skillPath);

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial, extensionManager: extensionManager, promptTemplates: promptTemplates);

        runtime.BindExtensionRuntime();

        var commands = await runtime.ExtensionBinding.GetCommandsAsync(CancellationToken.None);
        Assert.Contains(commands, command => command is { Name: "sample", Source: "extension" } && command.SourceInfo.Source == "extension:sample");
        Assert.Contains(commands, command => command is { Name: "prompt:release", Source: "prompt" } && command.SourceInfo.Scope == "project");
        Assert.Contains(commands, command => command is { Name: "skill:research", Source: "skill" } && command.SourceInfo.Path == skillPath);
    }

    [Fact]
    public async Task BindExtensionRuntimePopulatesResourceItemsFromRuntimeResources()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var skillDir = Path.Combine(root, "skills");
        Directory.CreateDirectory(skillDir);
        var skillMdPath = Path.Combine(skillDir, "SKILL.md");
        await File.WriteAllTextAsync(skillMdPath, "skill content");

        var promptPath = Path.Combine(root, "test-prompt.md");
        var themePath = Path.Combine(root, "theme.yaml");
        var contextPath = Path.Combine(root, "AGENTS.md");
        var systemPromptPath = Path.Combine(root, "SYSTEM.md");
        var packagePath = Path.Combine(root, "npm-package");
        Directory.CreateDirectory(packagePath);
        var packageJsonPath = Path.Combine(packagePath, "package.json");
        await File.WriteAllTextAsync(packageJsonPath, """{"name":"test-pkg"}""");
        await File.WriteAllTextAsync(promptPath, "prompt template content");
        await File.WriteAllTextAsync(themePath, "theme content");
        await File.WriteAllTextAsync(contextPath, "context content");
        await File.WriteAllTextAsync(systemPromptPath, "system prompt content");

        var skill = new Skill("test", "Test skill", "content", skillMdPath);

        var resources = new PiResources(
            ExtensionPaths: [],
            SkillPaths: [skillDir],
            PromptTemplatePaths: [promptPath],
            ThemePaths: [themePath],
            ContextFilePaths: [contextPath],
            SystemPromptPaths: [systemPromptPath],
            Packages: [new PiResolvedPackage("test-pkg", packagePath, "local")],
            Diagnostics: []
        );

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);

        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));

        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial, resources: resources);

        runtime.BindExtensionRuntime();

        var items = runtime.ExtensionBinding.ResourceItems;
        Assert.Contains(items, item => item.Kind == "skill" && item.Path == skillMdPath);
        Assert.Contains(items, item => item.Kind == "prompt" && item.Path == promptPath);
        Assert.Contains(items, item => item.Kind == "theme" && item.Path == themePath);
        Assert.Contains(items, item => item.Kind == "context" && item.Path == contextPath);
        Assert.Contains(items, item => item.Kind == "system-prompt" && item.Path == systemPromptPath);
        Assert.Contains(items, item => item.Kind == "package" && item.Path == packageJsonPath);

        Assert.DoesNotContain(items, item => item.Kind == "skill" && item.Path == skillDir);
        Assert.DoesNotContain(items, item => item.Kind == "package" && item.Path == packagePath);

        foreach (var item in items)
        {
            var read = await runtime.ExtensionBinding.ReadResourceAsync(item.Path, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Equal(item.Path, read.Path);
        }

        var unknown = await runtime.ExtensionBinding.ReadResourceAsync(Path.Combine(root, "unknown.txt"), CancellationToken.None);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task BindExtensionRuntimeDeduplicatesIdenticalSkillResourcePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var skillFilePath = Path.Combine(root, "SKILL.md");
        await File.WriteAllTextAsync(skillFilePath, "skill content");

        var skill = new Skill("test", "Test", "body", skillFilePath);

        var resources = new PiResources(
            ExtensionPaths: [],
            SkillPaths: [skillFilePath],
            PromptTemplatePaths: [],
            ThemePaths: [],
            ContextFilePaths: [],
            SystemPromptPaths: [],
            Packages: [],
            Diagnostics: []
        );

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);

        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));

        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial, resources: resources);

        runtime.BindExtensionRuntime();

        var items = runtime.ExtensionBinding.ResourceItems;
        var skillItems = items.Where(item => item.Kind == "skill" && item.Path == skillFilePath).ToList();
        Assert.Single(skillItems);
    }

    [Fact]
    public async Task ReadResourceAsyncPropagatesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var promptPath = Path.Combine(root, "test-prompt.md");
        await File.WriteAllTextAsync(promptPath, "prompt template content");

        var resources = new PiResources(
            ExtensionPaths: [],
            SkillPaths: [],
            PromptTemplatePaths: [promptPath],
            ThemePaths: [],
            ContextFilePaths: [],
            SystemPromptPaths: [],
            Packages: [],
            Diagnostics: []
        );

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, resources: resources);

        runtime.BindExtensionRuntime();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            runtime.ExtensionBinding.ReadResourceAsync(promptPath, cts.Token));
    }

    [Fact]
    public async Task BindExtensionRuntimeIncludesReplayedExtensionSkillResources()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-replayed-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skillFilePath = Path.Combine(root, "replayed-skill.md");
        await File.WriteAllTextAsync(skillFilePath, "replayed skill content");

        var registry = new ExtensionRegistry();
        registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("replayed_skill", "Replayed skill", "body", skillFilePath));
        var manager = new ExtensionManager(registry);

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();

        Assert.Contains(runtime.ExtensionBinding.ResourceItems, item => item.Kind == "skill" && item.Path == skillFilePath);
        var content = await runtime.ExtensionBinding.ReadResourceAsync(skillFilePath, CancellationToken.None);
        Assert.NotNull(content);
        Assert.Equal("replayed skill content", content.Content);
    }

    [Fact]
    public async Task BindExtensionRuntimeRefreshesResourcesWhenExtensionSkillsChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-dynamic-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skillFilePath = Path.Combine(root, "dynamic-skill.md");
        await File.WriteAllTextAsync(skillFilePath, "dynamic skill content");

        var registry = new ExtensionRegistry();
        var manager = new ExtensionManager(registry);

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, initial, extensionManager: manager);

        runtime.BindExtensionRuntime();

        Assert.DoesNotContain(runtime.ExtensionBinding.ResourceItems, item => item.Path == skillFilePath);

        var handle = registry.RegisterSkill("extension:test", new ExtensionSkillRegistration("dynamic_skill", "Dynamic skill", "body", skillFilePath));

        Assert.Contains(runtime.ExtensionBinding.ResourceItems, item => item.Kind == "skill" && item.Path == skillFilePath);
        var content = await runtime.ExtensionBinding.ReadResourceAsync(skillFilePath, CancellationToken.None);
        Assert.NotNull(content);
        Assert.Equal("dynamic skill content", content.Content);

        handle.Dispose();

        Assert.DoesNotContain(runtime.ExtensionBinding.ResourceItems, item => item.Path == skillFilePath);
        var afterRemove = await runtime.ExtensionBinding.ReadResourceAsync(skillFilePath, CancellationToken.None);
        Assert.Null(afterRemove);
    }

    [Fact]
    public async Task SwitchSessionAsyncRefreshesResourceBindingForReplacementHarnessSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-session-resource-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var target = await repo.CreateAsync(createOptions with { Id = "target" });
        await target.AppendMessageAsync(AgentMessages.User("target"));

        AgentHarness<JsonlSessionMetadata> HarnessWithSessionSkill(ISession<JsonlSessionMetadata> session)
        {
            var skillFilePath = Path.Combine(root, $"{session.Metadata.Id}-skill.md");
            File.WriteAllText(skillFilePath, $"skill content for {session.Metadata.Id}");
            var skill = new Skill("session_skill", "Session skill", "body", skillFilePath);
            return new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));
        }

        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSessionSkill, initial);
        runtime.BindExtensionRuntime();
        var initialSkillFilePath = Path.Combine(root, $"{initial.Metadata.Id}-skill.md");
        var targetSkillFilePath = Path.Combine(root, "target-skill.md");

        Assert.Contains(runtime.ExtensionBinding.ResourceItems, item => item.Kind == "skill" && item.Path == initialSkillFilePath);

        await runtime.SwitchSessionAsync(target.Metadata);

        Assert.DoesNotContain(runtime.ExtensionBinding.ResourceItems, item => item.Path == initialSkillFilePath);
        Assert.Contains(runtime.ExtensionBinding.ResourceItems, item => item.Kind == "skill" && item.Path == targetSkillFilePath);
        Assert.Null(await runtime.ExtensionBinding.ReadResourceAsync(initialSkillFilePath, CancellationToken.None));
        var targetContent = await runtime.ExtensionBinding.ReadResourceAsync(targetSkillFilePath, CancellationToken.None);
        Assert.NotNull(targetContent);
        Assert.Equal("skill content for target", targetContent.Content);
    }

    [Fact]
    public async Task BindExtensionRuntimeListsHarnessSkillResourcesWithoutPiResources()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-no-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skillFilePath = Path.Combine(root, "harness-skill.md");
        await File.WriteAllTextAsync(skillFilePath, "harness skill content");

        var skill = new Skill("harness_skill", "Harness skill", "body", skillFilePath);
        AgentHarness<JsonlSessionMetadata> HarnessWithSkill(ISession<JsonlSessionMetadata> session)
            => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Skills: [skill]));

        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, HarnessWithSkill, initial);

        runtime.BindExtensionRuntime();

        Assert.Contains(runtime.ExtensionBinding.ResourceItems, item => item.Kind == "skill" && item.Path == skillFilePath);
        var content = await runtime.ExtensionBinding.ReadResourceAsync(skillFilePath, CancellationToken.None);
        Assert.NotNull(content);
        Assert.Equal("harness skill content", content.Content);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root containing PiSharp.sln.");
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test", ThinkingLevelMap: new Dictionary<string, int> { ["off"] = 0 }), FakeStream, FakeCompletion, []));

    private static AgentHarness<JsonlSessionMetadata> ReasoningHarness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, ReasoningModel, FakeStream, FakeCompletion, []));

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }

    private sealed class RecordingLoggerFactory(RecordingLoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private static ModelDescriptor ReasoningModel => new(
        "test",
        "reasoning",
        "test",
        Reasoning: true,
        ThinkingLevelMap: new Dictionary<string, int>
        {
            ["minimal"] = 128,
            ["low"] = 512,
            ["medium"] = 1024,
            ["high"] = 2048,
            ["xhigh"] = 4096
        });

    private static async Task WaitForCommandAsync(ExtensionRegistry registry, string name, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (registry.Commands.Any(command => command.Value.Name == name)) return;
            await Task.Delay(25);
        }

        Assert.Contains(registry.Commands, command => command.Value.Name == name);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private sealed class RuntimeTestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public string Description => name;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;
        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
            => Task.FromResult(new AgentToolResult<object?>([new TextContent($"{name} result")], new { name }));
    }

    private sealed class CountingSession(ISession<JsonlSessionMetadata> inner) : ISession<JsonlSessionMetadata>
    {
        public int GetEntriesCount { get; private set; }
        public JsonlSessionMetadata Metadata => inner.Metadata;
        public string Id => inner.Id;
        public string? LeafId { get => inner.LeafId; set => inner.LeafId = value; }
        public ISessionStorage<JsonlSessionMetadata> Storage => inner.Storage;
        public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => inner.GetLeafIdAsync(cancellationToken);
        public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => inner.GetEntryAsync(id, cancellationToken);
        public Task<IReadOnlyList<SessionTreeEntry>> GetBranchAsync(string? fromId = null, CancellationToken cancellationToken = default) => inner.GetBranchAsync(fromId, cancellationToken);
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

        public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
        {
            GetEntriesCount++;
            return inner.GetEntriesAsync(cancellationToken);
        }
    }

    private sealed class BlockingAppendStorage<TMetadata>(TMetadata metadata) : ISessionStorage<TMetadata>
        where TMetadata : ISessionMetadata
    {
        private readonly MemorySessionStorage<TMetadata> _inner = new(metadata);
        private readonly TaskCompletionSource _appendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAppend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AppendStarted => _appendStarted.Task;

        public void ReleaseAppend() => _releaseAppend.TrySetResult();

        public Task<TMetadata> GetMetadataAsync(CancellationToken cancellationToken = default) => _inner.GetMetadataAsync(cancellationToken);

        public Task<string?> GetLeafIdAsync(CancellationToken cancellationToken = default) => _inner.GetLeafIdAsync(cancellationToken);

        public Task SetLeafIdAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.SetLeafIdAsync(leafId, cancellationToken);

        public Task<string> CreateEntryIdAsync(CancellationToken cancellationToken = default) => _inner.CreateEntryIdAsync(cancellationToken);

        public async Task AppendEntryAsync(SessionTreeEntry entry, CancellationToken cancellationToken = default)
        {
            _appendStarted.TrySetResult();
            await _releaseAppend.Task.WaitAsync(cancellationToken);
            await _inner.AppendEntryAsync(entry, cancellationToken);
        }

        public Task<SessionTreeEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default) => _inner.GetEntryAsync(id, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> FindEntriesAsync(Func<SessionTreeEntry, bool> predicate, CancellationToken cancellationToken = default) => _inner.FindEntriesAsync(predicate, cancellationToken);

        public Task<string?> GetLabelAsync(string id, CancellationToken cancellationToken = default) => _inner.GetLabelAsync(id, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetPathToRootAsync(string? leafId, CancellationToken cancellationToken = default) => _inner.GetPathToRootAsync(leafId, cancellationToken);

        public Task<IReadOnlyList<SessionTreeEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) => _inner.GetEntriesAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}

file sealed class NonPumpingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
    }
}
