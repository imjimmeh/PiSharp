using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.IO;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

public sealed class PiRuntimeBootstrapTests
{
    [Fact]
    public async Task CreateRuntimeRestoresPersistedThinkingLevelAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var model = new RuntimeModelOptions("amazon-bedrock", "anthropic.claude-haiku-4-5-20251001-v1:0");
        var resources = new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true);

        await using (var first = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            HomeDirectory: home,
            Model: model,
            Resources: resources)))
        {
            await first.SetThinkingLevelAsync(ThinkingLevel.Medium);
            Assert.Equal(ThinkingLevel.Medium, first.Harness.ThinkingLevel);
        }

        await using var restarted = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            HomeDirectory: home,
            Resources: resources));

        Assert.Equal(ThinkingLevel.Medium, restarted.Harness.ThinkingLevel);
    }

    [Fact]
    public async Task CreateRuntimeUsesExplicitThinkingOverPersistedAndSessionDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        var sessionsRoot = Path.Combine(root, "sessions");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"), "{\"defaultThinking\":\"medium\"}\n");
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), sessionsRoot);
        var session = await repo.CreateAsync(new JsonlSessionCreateOptions(repoRoot));
        await session.AppendThinkingLevelChangeAsync("high");
        var resources = new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            SessionsRoot: sessionsRoot,
            HomeDirectory: home,
            Model: new RuntimeModelOptions("amazon-bedrock", "anthropic.claude-haiku-4-5-20251001-v1:0", ThinkingLevel.Low),
            Resources: resources,
            Session: new RuntimeSessionStartupOptions(ContinueLatestForCwd: true)));

        Assert.Equal(ThinkingLevel.Low, runtime.Harness.ThinkingLevel);
    }

    [Fact]
    public async Task CreateRuntimeRestoresResumedSessionThinkingLevelOverPersistedDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        var sessionsRoot = Path.Combine(root, "sessions");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(home, ".pi", "PiSharp"));
        await File.WriteAllTextAsync(Path.Combine(home, ".pi", "PiSharp", "settings.json"), "{\"defaultThinking\":\"off\"}\n");
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(repoRoot), sessionsRoot);
        var session = await repo.CreateAsync(new JsonlSessionCreateOptions(repoRoot));
        await session.AppendThinkingLevelChangeAsync("medium");
        // A user message is required to persist the session file. Sessions with no user messages
        // are not written to disk (they are "empty sessions" that would otherwise pollute the list).
        await session.AppendMessageAsync(AgentMessages.User("hello"));
        var resources = new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            SessionsRoot: sessionsRoot,
            HomeDirectory: home,
            Model: new RuntimeModelOptions("amazon-bedrock", "anthropic.claude-haiku-4-5-20251001-v1:0"),
            Resources: resources,
            Session: new RuntimeSessionStartupOptions(ContinueLatestForCwd: true)));

        Assert.Equal(ThinkingLevel.Medium, runtime.Harness.ThinkingLevel);
    }

    [Fact]
    public async Task CreateRuntimeUsesProvidedSessionRootAndCreatesSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = new SystemExecutionEnv(root);
        var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            SessionsRoot: Path.Combine(root, "custom-sessions"),
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true)));

        Assert.Equal(Path.GetFullPath(root), runtime.Session.Metadata.Cwd);
        Assert.Contains("custom-sessions", runtime.Session.Metadata.Path);
    }

    [Fact]
    public async Task CreateRuntimeLoadsPromptTemplateCatalogAndThemeDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "prompts"));
        Directory.CreateDirectory(Path.Combine(root, "themes"));
        await File.WriteAllTextAsync(Path.Combine(root, "prompts", "release.md"), "---\ndescription: Release notes\n---\nRelease $1");
        await File.WriteAllTextAsync(Path.Combine(root, "themes", "team.json"), "{\"name\":\"Dim Team\",\"tokens\":{\"accent\":\"#ffffff\"}}");
        var env = new SystemExecutionEnv(root);

        var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                PromptTemplatePaths: ["./prompts"],
                ThemePaths: ["./themes"],
                DisableExtensions: true,
                DisableSkills: true,
                DisableContextFiles: true)));

        Assert.Contains(runtime.PromptTemplates.Templates, template => template.Name == "release" && template.Description == "Release notes");
        Assert.Equal("Release 1.2.3", runtime.PromptTemplates.FormatInvocation("release", ["1.2.3"]));
        Assert.Equal("Dim Team", runtime.Theme?.Name);
    }

    [Fact]
    public async Task CreateRuntimeActivatesToolsRegisteredFromTypeScriptSessionStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "dynamic-tools.ts"), """
            export default function activate(pi) {
              pi.on('session_start', (_event, ctx) => {
                pi.registerTool({
                  name: 'session_tool',
                  label: 'Session Tool',
                  description: 'Tool registered during session_start',
                  promptSnippet: 'Run session-start tool behavior',
                  parameters: { type: 'object', properties: {} },
                  execute: async () => ({ content: [{ type: 'text', text: 'ok' }], details: {} })
                });
                ctx.ui.notify('registered session_tool', 'info');
              });
            }
            """);
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true)));

        Assert.NotNull(runtime.TsHost);
        Assert.Contains(runtime.Resources!.ExtensionPaths, path => path.EndsWith("dynamic-tools.ts", StringComparison.Ordinal));
        Assert.Contains("session_tool", runtime.Harness.AllToolNames);
        Assert.Contains("session_tool", runtime.Harness.ActiveToolNames);
    }

    [Fact]
    public async Task CreateRuntimeCapturesStartupBenchmarkWhenEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true),
            BenchmarkStartup: true));

        var benchmark = runtime.StartupBenchmark;
        Assert.NotNull(benchmark);
        Assert.True(benchmark!.Total >= TimeSpan.Zero);
        Assert.Contains(benchmark.Phases, phase => phase.Name == "settings.load");
        Assert.Contains(benchmark.Phases, phase => phase.Name == "providers.register");
        Assert.Contains(benchmark.Phases, phase => phase.Name == "resources.load");
        Assert.Contains(benchmark.Phases, phase => phase.Name == "session.resolve");
        Assert.Contains(benchmark.Phases, phase => phase.Name == "tools.resolve");
    }

    [Fact]
    public async Task CreateRuntimeCapturesEveryTypeScriptExtensionTimingWhenBenchmarkEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "a.ts"), "export default function activate() {}");
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "b.ts"), "export default function activate() {}");
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true),
            BenchmarkStartup: true));

        var benchmark = runtime.StartupBenchmark;
        Assert.NotNull(benchmark);
        Assert.Contains(benchmark!.TypeScriptExtensions, extension => extension.Path.EndsWith("a.ts", StringComparison.Ordinal));
        Assert.Contains(benchmark.TypeScriptExtensions, extension => extension.Path.EndsWith("b.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateRuntimeLoadsIndependentTypeScriptExtensionsConcurrently()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", $"slow-{i}.ts"), $$"""
                export default async function activate(pi) {
                  await new Promise(resolve => setTimeout(resolve, 500));
                  pi.registerCommand('slow_{{i}}', { description: 'slow {{i}}', handler: () => 'ok' });
                }
                """);
        }
        var env = new SystemExecutionEnv(root);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true),
            BenchmarkStartup: true));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1_800), $"Startup took {stopwatch.Elapsed.TotalMilliseconds:F0}ms, which indicates extension activation was sequential.");
        Assert.Contains(runtime.ExtensionManager!.Registry.Commands, command => command.Value.Name == "slow_0");
        Assert.Contains(runtime.ExtensionManager.Registry.Commands, command => command.Value.Name == "slow_1");
        Assert.Contains(runtime.ExtensionManager.Registry.Commands, command => command.Value.Name == "slow_2");
    }

    [Fact]
    public async Task CreateRuntimeUsesDescriptorReplayAndBackgroundActivationWhenWarm()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var extensionPath = Path.Combine(root, ".pi", "extensions", "warm-tools.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              await new Promise(resolve => setTimeout(resolve, 750));
              pi.registerTool({
                name: 'warm_tool',
                label: 'Warm Tool',
                description: 'Tool registered after warm descriptor replay',
                parameters: { type: 'object', properties: {} },
                execute: async () => ({ content: [{ type: 'text', text: 'ok' }], details: {} })
              });
            }
            """);
        var env = new SystemExecutionEnv(root);
        var options = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        await using (var cold = await PiRuntimeBootstrap.CreateRuntimeAsync(options))
        {
            Assert.Contains("warm_tool", cold.Harness.AllToolNames);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var warm = await PiRuntimeBootstrap.CreateRuntimeAsync(options);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Warm startup took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
        Assert.Contains(warm.ExtensionLoadCoordinator.Statuses, status => status.ExtensionPath == extensionPath && status.State is ExtensionLoadState.DescriptorReplayed or ExtensionLoadState.BackgroundLoading or ExtensionLoadState.Ready);
        Assert.Contains("warm_tool", warm.Harness.AllToolNames);
    }

    [Fact]
    public async Task WarmDescriptorReplayBackgroundActivationDoesNotBlockInputReadiness()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var extensionPath = Path.Combine(root, ".pi", "extensions", "warm-background.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              await new Promise(resolve => setTimeout(resolve, 1_000));
              pi.registerTool({
                name: 'warm_background_tool',
                label: 'Warm Background Tool',
                description: 'Tool registered after background warm activation',
                parameters: { type: 'object', properties: {} },
                execute: async () => ({ content: [{ type: 'text', text: 'ok' }], details: {} })
              });
            }
            """);
        var env = new SystemExecutionEnv(root);
        var options = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        await using (var cold = await PiRuntimeBootstrap.CreateRuntimeAsync(options))
        {
            Assert.Contains("warm_background_tool", cold.Harness.AllToolNames);
        }

        await using var warm = await PiRuntimeBootstrap.CreateRuntimeAsync(options);

        var status = Assert.Single(warm.ExtensionLoadCoordinator.Statuses, item => item.ExtensionPath == extensionPath);
        Assert.True(status.State is ExtensionLoadState.DescriptorReplayed or ExtensionLoadState.BackgroundLoading or ExtensionLoadState.Ready);
        Assert.False(warm.GetExtensionLoadSummary().BlocksInput);
        await warm.ExtensionLoadCoordinator.ExtensionsReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("warm_background_tool", warm.Harness.AllToolNames);
    }

    [Fact]
    public async Task CreateRuntimeMarksTypeScriptExtensionsFailedWhenBridgeStartupLoadFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var extensionPath = Path.Combine(root, ".pi", "extensions", "bridge-exit.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default function activate() {
              process.exit(42);
            }
            """);
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true)));

        var status = Assert.Single(runtime.ExtensionLoadCoordinator.Statuses, item => item.ExtensionPath == extensionPath);
        Assert.Equal(ExtensionLoadState.Failed, status.State);
        Assert.False(runtime.GetExtensionLoadSummary().IsLoading);
    }

    [Fact]
    public async Task WarmDescriptorReplayDoesNotQueueSlowActivationAheadOfCachedCommandInvocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var slowExtensionPath = Path.Combine(root, ".pi", "extensions", "a-slow-warm-replay.ts");
        await File.WriteAllTextAsync(slowExtensionPath, """
            export default async function activate(pi) {
              await new Promise(resolve => setTimeout(resolve, 5_000));
              pi.registerCommand('slow_warm_replay', { description: 'slow warm replay', handler: () => 'ok' });
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "b-fast-warm-replay.ts"), """
            export default function activate(pi) {
              pi.registerCommand('fast_warm_replay', { description: 'fast warm replay', handler: () => 'ok' });
            }
            """);
        var env = new SystemExecutionEnv(root);
        var options = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        SessionRuntime? cold = null;
        try
        {
            cold = await PiRuntimeBootstrap.CreateRuntimeAsync(options).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains(cold.ExtensionManager!.Registry.Commands, command => command.Value.Name == "slow_warm_replay");
            Assert.Contains(cold.ExtensionManager.Registry.Commands, command => command.Value.Name == "fast_warm_replay");
        }
        finally
        {
            if (cold is not null) await cold.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }

        SessionRuntime? warm = null;
        try
        {
            warm = await PiRuntimeBootstrap.CreateRuntimeAsync(options).WaitAsync(TimeSpan.FromSeconds(15));
            var command = Assert.Single(warm.ExtensionManager!.Registry.Commands, command => command.Value.Name == "fast_warm_replay");

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await command.Value.Handler("", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            elapsed.Stop();

            var slowStatus = Assert.Single(warm.ExtensionLoadCoordinator.Statuses, status => status.ExtensionPath == slowExtensionPath);
            Assert.NotEqual(ExtensionLoadState.Failed, slowStatus.State);
            Assert.NotEqual(ExtensionLoadState.Ready, slowStatus.State);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Fast cached command took {elapsed.Elapsed.TotalMilliseconds:F0}ms.");
        }
        finally
        {
            if (warm is not null) await warm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task WarmDescriptorReplayStillActivatesCachedExtensionAfterStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var markerPath = Path.Combine(root, "activated.txt").Replace("\\", "\\\\");
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "cached-side-effect.ts"), $$"""
            export default function activate(pi) {
              pi.registerCommand('cached_side_effect', { description: 'cached side effect', handler: () => 'ok' });
              pi.exec('dotnet', ['--version']).then(() => import('node:fs/promises').then(fs => fs.writeFile('{{markerPath}}', 'activated')));
            }
            """);
        var env = new SystemExecutionEnv(root);
        var options = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        await using (var cold = await PiRuntimeBootstrap.CreateRuntimeAsync(options))
        {
            Assert.Contains(cold.ExtensionManager!.Registry.Commands, command => command.Value.Name == "cached_side_effect");
            await cold.ExtensionLoadCoordinator.ExtensionsReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (File.Exists(Path.Combine(root, "activated.txt"))) File.Delete(Path.Combine(root, "activated.txt"));

        await using var warm = await PiRuntimeBootstrap.CreateRuntimeAsync(options);

        await warm.ExtensionLoadCoordinator.ExtensionsReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        var status = Assert.Single(warm.ExtensionLoadCoordinator.Statuses);
        Assert.Equal(ExtensionLoadState.Ready, status.State);
        await WaitForFileExistsAsync(Path.Combine(root, "activated.txt"), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WarmDescriptorReplayCanDeferActivationUntilCallerStartsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var markerFile = Path.Combine(root, "deferred-activated.txt");
        var markerPath = markerFile.Replace("\\", "\\\\");
        var extensionPath = Path.Combine(root, ".pi", "extensions", "deferred-side-effect.ts");
        await File.WriteAllTextAsync(extensionPath, $$"""
            export default function activate(pi) {
              pi.registerCommand('deferred_cached_side_effect', { description: 'deferred cached side effect', handler: () => 'ok' });
              pi.exec('dotnet', ['--version']).then(() => import('node:fs/promises').then(fs => fs.writeFile('{{markerPath}}', 'activated')));
            }
            """);
        var env = new SystemExecutionEnv(root);
        var coldOptions = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        await using (var cold = await PiRuntimeBootstrap.CreateRuntimeAsync(coldOptions))
        {
            Assert.Contains(cold.ExtensionManager!.Registry.Commands, command => command.Value.Name == "deferred_cached_side_effect");
            await cold.ExtensionLoadCoordinator.ExtensionsReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        if (File.Exists(markerFile)) File.Delete(markerFile);

        var warmOptions = coldOptions with
        {
            Extensions = new RuntimeExtensionOptions(DeferCachedActivationUntilUiReady: true)
        };
        await using var warm = await PiRuntimeBootstrap.CreateRuntimeAsync(warmOptions);

        var status = Assert.Single(warm.ExtensionLoadCoordinator.Statuses, item => item.ExtensionPath == extensionPath);
        Assert.Equal(ExtensionLoadState.DescriptorReplayed, status.State);
        Assert.False(File.Exists(markerFile));

        await warm.StartCachedExtensionBackgroundActivationAsync(CancellationToken.None);
        await warm.ExtensionLoadCoordinator.ExtensionsReadyTask.WaitAsync(TimeSpan.FromSeconds(5));

        status = Assert.Single(warm.ExtensionLoadCoordinator.Statuses, item => item.ExtensionPath == extensionPath);
        Assert.Equal(ExtensionLoadState.Ready, status.State);
        await WaitForFileExistsAsync(markerFile, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WarmBackgroundActivationDoesNotBlockLaterExtensionsBehindSlowOrderedExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "a-fast.ts"), "export default function activate(pi) { pi.registerCommand('early_fast_a', { description: 'a', handler: () => 'ok' }); }");
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "b-fast.ts"), "export default function activate(pi) { pi.registerCommand('early_fast_b', { description: 'b', handler: () => 'ok' }); }");
        var slowPath = Path.Combine(root, ".pi", "extensions", "c-slow-ordered.ts");
        await File.WriteAllTextAsync(slowPath, """
            export default async function activate(pi) {
              pi.on('session_shutdown', () => {});
              await new Promise(resolve => setTimeout(resolve, 5_000));
              pi.registerCommand('slow_ordered', { description: 'slow', handler: () => 'ok' });
            }
            """);
        var laterPath = Path.Combine(root, ".pi", "extensions", "d-later-fast.ts");
        await File.WriteAllTextAsync(laterPath, "export default function activate(pi) { pi.registerCommand('later_fast', { description: 'later', handler: () => 'ok' }); }");
        var env = new SystemExecutionEnv(root);
        var options = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        await using (var cold = await PiRuntimeBootstrap.CreateRuntimeAsync(options))
        {
            await cold.ExtensionLoadCoordinator.ExtensionsReadyTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(cold.ExtensionManager!.Registry.Commands, command => command.Value.Name == "later_fast");
        }

        await using var warm = await PiRuntimeBootstrap.CreateRuntimeAsync(options);

        await WaitForExtensionStateAsync(warm.ExtensionLoadCoordinator, laterPath, ExtensionLoadState.Ready, TimeSpan.FromSeconds(3));
        var slowStatus = Assert.Single(warm.ExtensionLoadCoordinator.Statuses, status => status.ExtensionPath == slowPath);
        Assert.NotEqual(ExtensionLoadState.Ready, slowStatus.State);
    }

    [Fact]
    public async Task ReloadExtensionsAfterDescriptorReplayKeepsCoherentFinalState()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var extensionPath = Path.Combine(root, ".pi", "extensions", "reload-during-background.ts");
        await File.WriteAllTextAsync(extensionPath, """
            export default async function activate(pi) {
              await new Promise(resolve => setTimeout(resolve, 500));
              pi.registerCommand('background_old', { description: 'old', handler: () => 'old' });
            }
            """);
        var env = new SystemExecutionEnv(root);
        var options = new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true));
        await using (var cold = await PiRuntimeBootstrap.CreateRuntimeAsync(options))
        {
            Assert.Contains(cold.ExtensionManager!.Registry.Commands, command => command.Value.Name == "background_old");
        }

        await using var warm = await PiRuntimeBootstrap.CreateRuntimeAsync(options);
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('background_new', { description: 'new', handler: () => 'new' }); }");

        await warm.ReloadExtensionsAsync(CancellationToken.None);

        Assert.DoesNotContain(warm.ExtensionManager!.Registry.Commands, command => command.Value.Name == "background_old");
        Assert.Contains(warm.ExtensionManager.Registry.Commands, command => command.Value.Name == "background_new");
    }

    [Fact]
    public async Task ReloadExtensionsReflectsEditedTypeScriptSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var extensionPath = Path.Combine(root, ".pi", "extensions", "reloadable.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('reload_one', { description: 'one', handler: () => 'one' }); }");
        var env = new SystemExecutionEnv(root);
        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true)));
        Assert.Contains(runtime.ExtensionManager!.Registry.Commands, command => command.Value.Name == "reload_one");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('reload_two', { description: 'two', handler: () => 'two' }); }");

        await runtime.ReloadExtensionsAsync(CancellationToken.None);

        Assert.DoesNotContain(runtime.ExtensionManager.Registry.Commands, command => command.Value.Name == "reload_one");
        Assert.Contains(runtime.ExtensionManager.Registry.Commands, command => command.Value.Name == "reload_two");
    }

    [Fact]
    public async Task ReloadExtensionsRepopulatesExtensionLoadCoordinatorStatuses()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        var extensionPath = Path.Combine(root, ".pi", "extensions", "reload-status.ts");
        await File.WriteAllTextAsync(extensionPath, "export default function activate(pi) { pi.registerCommand('reload_status', { description: 'status', handler: () => 'ok' }); }");
        var env = new SystemExecutionEnv(root);
        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true)));

        await runtime.ReloadExtensionsAsync(CancellationToken.None);

        var status = Assert.Single(runtime.ExtensionLoadCoordinator.Statuses, item => string.Equals(item.ExtensionPath, extensionPath, StringComparison.Ordinal));
        Assert.True(status.State is ExtensionLoadState.Ready or ExtensionLoadState.Failed);
        Assert.NotEqual(ExtensionLoadState.Stale, status.State);
    }

    [Fact]
    public async Task CreateRuntimeCapturesPerTypeScriptExtensionTimingWhenBenchmarkEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "extensions", "benchmark-tools.ts"), "export default function activate() {}");
        var env = new SystemExecutionEnv(root);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            env,
            HomeDirectory: root,
            Resources: new RuntimeResourceOptions(
                DisableSkills: true,
                DisablePromptTemplates: true,
                DisableThemes: true,
                DisableContextFiles: true),
            BenchmarkStartup: true));

        var benchmark = runtime.StartupBenchmark;
        Assert.NotNull(benchmark);
        var timing = Assert.Single(benchmark!.TypeScriptExtensions, extension => extension.Path.EndsWith("benchmark-tools.ts", StringComparison.Ordinal));
        Assert.NotNull(timing.BridgeTimings);
        Assert.True(timing.BridgeTimings!.Total >= 0);
        Assert.True(timing.BridgeTimings.CompilerLoad >= 0);
        Assert.True(timing.BridgeTimings.Transpile >= 0);
        Assert.True(timing.BridgeTimings.ModuleImport >= 0);
    }
    [Fact]
    public async Task CreateRuntimeWiresPackageApiAndManagedSkillStoreIntoExtensionBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var resources = new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            HomeDirectory: home,
            Resources: resources));

        // Packages surface is backed by RuntimePackageService (returns installed entries; empty here).
        var installed = await runtime.ExtensionBinding.Packages.ListAsync();
        Assert.Empty(installed);

        // ManagedSkills surface is backed by ManagedSkillStore at ~/.pi/PiSharp/managed-skills.
        var created = await runtime.ExtensionBinding.Skills.ManagedSkills.CreateAsync(
            new ManagedSkillCreateRequest("bound-skill", "Bound skill", "bound-body"));
        Assert.Equal("bound-skill", created.Name);
        Assert.Equal("managed", created.Source);
        Assert.Equal(5, created.SourcePriority);

        var registered = Assert.Single(runtime.ExtensionManager!.Registry.Skills, skill => skill.Value.Name == "bound-skill").Value;
        Assert.Equal("managed", registered.Source);
        Assert.Equal(5, registered.SourcePriority);
        Assert.Equal("bound-body", registered.Content);

        var listed = await runtime.ExtensionBinding.Skills.ManagedSkills.ListAsync();
        Assert.Equal("bound-skill", Assert.Single(listed).Name);

        // The managed-skill pack survives restart at the home directory.
        await using var restarted = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            HomeDirectory: home,
            Resources: resources));
        var restartedListed = await restarted.ExtensionBinding.Skills.ManagedSkills.ListAsync();
        Assert.Equal("bound-skill", Assert.Single(restartedListed).Name);
    }

    [Fact]
    public async Task CreateRuntimeBindsSkillProviderRegistrationIntoRegistry()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-runtime-bootstrap-" + Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);
        var resources = new RuntimeResourceOptions(DisableExtensions: true, DisableSkills: true, DisablePromptTemplates: true, DisableThemes: true, DisableContextFiles: true);

        await using var runtime = await PiRuntimeBootstrap.CreateRuntimeAsync(new PiRuntimeOptions(
            new SystemExecutionEnv(repoRoot),
            HomeDirectory: root,
            Resources: resources));

        var handle = await runtime.ExtensionBinding.RegisterSkillProviderAsync(
            new TestSkillProvider("bound-provider", 4), CancellationToken.None);
        try
        {
            Assert.Contains(runtime.ExtensionManager!.Registry.SkillProviders, provider => provider.Value.Name == "bound-provider");
            var priorities = await runtime.ExtensionBinding.GetSkillProviderPrioritiesAsync(CancellationToken.None);
            Assert.Equal(4, priorities["bound-provider"]);
        }
        finally
        {
            handle.Dispose();
        }

        Assert.DoesNotContain(runtime.ExtensionManager!.Registry.SkillProviders, provider => provider.Value.Name == "bound-provider");
    }

    private sealed class TestSkillProvider(string name, int priority) : ISkillProvider
    {
        public string Name { get; } = name;
        public int Priority { get; } = priority;
        public Task<IReadOnlyList<ExtensionSkillDefinition>> DiscoverAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtensionSkillDefinition>>([]);
    }


    private static async Task WaitForExtensionStateAsync(ExtensionLoadCoordinator coordinator, string extensionPath, ExtensionLoadState expectedState, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ExtensionLoadStatus? lastStatus = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastStatus = coordinator.Statuses.SingleOrDefault(status => status.ExtensionPath == extensionPath);
            if (lastStatus?.State == expectedState)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Extension {extensionPath} did not reach {expectedState} within {timeout.TotalMilliseconds:F0}ms. Last state: {lastStatus?.State.ToString() ?? "missing"}.");
    }

    private static async Task WaitForFileExistsAsync(string filePath, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(filePath)) return;

            await Task.Delay(50);
        }

        Assert.Fail($"File {filePath} did not exist within {timeout.TotalMilliseconds:F0}ms.");
    }

}
