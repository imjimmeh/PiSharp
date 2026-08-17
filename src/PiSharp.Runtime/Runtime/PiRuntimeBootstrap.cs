using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Agent.Sessions;
using PiSharp.Ai;
using PiSharp.Ai.Auth;
using PiSharp.Compatibility.Resources;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;
using PiSharp.Permissions;
using PiSharp.PluginHost;
using PiSharp.TsBridge;
using ISystemPromptComposer = PiSharp.Agent.Core.Prompting.ISystemPromptComposer;

namespace PiSharp.Runtime;

public static class PiRuntimeBootstrap
{
    private static readonly object ProviderRegistrationGate = new();
    private static bool _providersRegistered;

    private const int ExtensionLoadTimeoutMinutes = 2;

    public static async Task<SessionRuntime> CreateRuntimeAsync(PiRuntimeOptions options, ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var benchmark = options.BenchmarkStartup ? new StartupBenchmarkCollector() : null;
        var startupContext = new RuntimeStartupContext(options, benchmark);
        var logger = loggerFactory?.CreateLogger("PiSharp.Runtime.Bootstrap") ?? NullLogger.Instance;
        var bootstrapStopwatch = Stopwatch.StartNew();
        bool requestedSessionIdOrPath = options.Session is not null
            && (!string.IsNullOrWhiteSpace(options.Session.SessionIdOrPath)
                || !string.IsNullOrWhiteSpace(options.Session.NewSessionId));
        logger.LogInformation($"bootstrap: create-session start — NoExtensions={options.Resources?.DisableExtensions == true}, NoTsExtensions={options.Resources?.DisableTypeScriptExtensions == true}, RequestedSessionIdOrPath={requestedSessionIdOrPath}");

        var settingsStore = new PiSettingsStore();
        var settings = await startupContext.MeasureAsync("settings.load", () => settingsStore.LoadAsync(options.Env.Cwd, options.HomeDirectory, cancellationToken: cancellationToken));
        var authStorage = new FileOAuthStorage(settings.Paths.AuthPath);
        var credentialResolver = options.CredentialResolver ?? new ProviderCredentialResolver(authStorage);
        RegisterBuiltInOAuthProviders();
        startupContext.Measure("providers.register", () =>
        {
            EnsureProvidersRegistered(options.HttpClient, credentialResolver, settings.Paths.ModelsPath);
            return true;
        });

        var resourcesOptions = options.Resources ?? new RuntimeResourceOptions();
        var resources = await startupContext.MeasureAsync("resources.load", () => new PiResourceLoader().LoadAsync(new PiResourceLoadRequest(
            settings,
            options.Env.Cwd,
            resourcesOptions.ExtensionPaths ?? [],
            resourcesOptions.SkillPaths ?? [],
            resourcesOptions.PromptTemplatePaths ?? [],
            resourcesOptions.ThemePaths ?? [],
            resourcesOptions.DisableExtensions,
            resourcesOptions.DisableSkills,
            resourcesOptions.DisablePromptTemplates,
            resourcesOptions.DisableThemes,
            resourcesOptions.DisableContextFiles,
            resourcesOptions.DisableTypeScriptExtensions), cancellationToken));

        var extensionDiagnostics = new List<RuntimeDiagnostic>();
        var (promptTemplateCatalog, promptTemplateDiagnostics) =
            await startupContext.MeasureAsync("prompt-templates.load", () => PromptTemplateCatalog.LoadAsync(options.Env, resources.PromptTemplatePaths, cancellationToken));
        extensionDiagnostics.AddRange(promptTemplateDiagnostics.Select(diagnostic =>
            new RuntimeDiagnostic(RuntimeDiagnosticType.Warning, $"Prompt template {diagnostic.Code} at {diagnostic.Path}: {diagnostic.Message}")));

        var (themeDocument, themeDiagnostics) = await startupContext.MeasureAsync("theme.load", () => TuiThemeDocument.LoadFirstAsync(resources.ThemePaths, cancellationToken));
        extensionDiagnostics.AddRange(themeDiagnostics.Select(diagnostic =>
            new RuntimeDiagnostic(RuntimeDiagnosticType.Warning, $"Theme {diagnostic.Code} at {diagnostic.Path}: {diagnostic.Message}")));

        var sessionOptions = options.Session ?? new RuntimeSessionStartupOptions();
        var sessionRoot = sessionOptions.SessionDirectory ?? options.SessionsRoot ?? settings.Settings.SessionDir ?? settings.Paths.SessionsRoot;
        var repo = new JsonlSessionRepo(options.Env, sessionRoot, writeLeafEntries: !options.CompatibilityMode, loggerFactory: loggerFactory);
        var createOptions = new JsonlSessionCreateOptions(options.Env.Cwd, sessionOptions.NewSessionId);
        var session = await startupContext.MeasureAsync("session.resolve", () => ResolveSessionAsync(repo, createOptions, sessionOptions, cancellationToken));
        var urlRegistry = new InternalUrlRegistry();
        var fileContentExtractorRegistry = new FileContentExtractorRegistry();
        var searchProviderRegistry = new SearchProviderRegistry();
        var tools = startupContext.Measure("tools.resolve", () => RuntimeToolSelector.Create(options.Env, options.Tools, urlRegistry: urlRegistry, contentExtractors: fileContentExtractorRegistry));

        var extensions = new ExtensionRegistry { BuiltInToolNames = tools.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) };
        var manager = new ExtensionManager(extensions);
        IReadOnlyList<string>? startupActiveToolNames = tools.ActiveToolNames;
        IReadOnlyList<string> StartupAllToolNames()
            => tools.Tools.Select(tool => tool.Name).Concat(extensions.Tools.Select(tool => tool.Value.Name)).Distinct(StringComparer.Ordinal).ToArray();
        var extensionBinding = new ExtensionRuntimeBinding(options.Env.Cwd, false, NoExtensionUi.Instance)
        {
            ExecutionEnv = new GatedExecutionEnv(options.Env),
            UrlRegistry = urlRegistry,
            FileContentExtractors = fileContentExtractorRegistry,
            SearchProviders = searchProviderRegistry,
            GetSessionIdAsync = _ => Task.FromResult<string?>(session.Metadata.Id),
            GetAllToolsAsync = _ => Task.FromResult(StartupAllToolNames()),
            GetActiveToolsAsync = _ => Task.FromResult(startupActiveToolNames ?? StartupAllToolNames()),
            SetActiveToolsAsync = (names, _) =>
            {
                startupActiveToolNames = names.Count == 0 ? null : names.ToArray();
                return Task.CompletedTask;
            },
            GetAllRulesAsync = ct => extensions.GetAllRulesAsync(ct),
            GetRuleProviderNamesAsync = _ => Task.FromResult(extensions.GetRuleProviderNames()),
        };
        var pluginHost = new NativePluginHost(PluginHostOptions.FromCwd(
            options.Env.Cwd,
            resources.ExtensionPaths.Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray(),
            settings.Paths.HomeDirectory));
        var extensionLoadCoordinator = new ExtensionLoadCoordinator(loggerFactory);
        var backgroundExtensionPaths = new List<string>();
        TsExtensionHost? tsHost = null;

        if (!resourcesOptions.DisableExtensions)
        {
            var extensionsStopwatch = benchmark is null ? null : Stopwatch.StartNew();
            try
            {
                var nativePaths = startupContext.Measure("extensions.native.discover", () => pluginHost.Discover().ToArray());
                logger.LogInformation($"phase: native discover ({nativePaths.Length} found)");
                foreach (var path in nativePaths)
                {
                    var loadStopwatch = Stopwatch.StartNew();
                    TimeSpan loadDuration;
                    TimeSpan initializeDuration;
                    try
                    {
                        var plugin = pluginHost.Load(path);
                        loadDuration = loadStopwatch.Elapsed;
                        var initStopwatch = Stopwatch.StartNew();
                        await manager.InitializeAsync(plugin.Descriptor, plugin.Extension, extensionBinding, cancellationToken);
                        initializeDuration = initStopwatch.Elapsed;
                        benchmark?.AddNativeExtension(path, loadDuration, initializeDuration, success: true);
                    }
                    catch (Exception exception)
                    {
                        loadDuration = loadStopwatch.Elapsed;
                        initializeDuration = TimeSpan.Zero;
                        benchmark?.AddNativeExtension(path, loadDuration, initializeDuration, success: false, exception.Message);
                        throw;
                    }
                }

                var tsExtensionPaths = resources.ExtensionPaths.Where(path => !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (!resourcesOptions.DisableTypeScriptExtensions && tsExtensionPaths is { Length: > 0 })
                {
                    tsHost = new TsExtensionHost(new TsBridgeOptions(
                        ExtensionPaths: [],
                        WorkingDirectory: options.Env.Cwd,
                        CacheDirectory: Path.Combine(settings.Paths.GlobalPiSharpDirectory, "cache", "ts-bridge")), extensions, extensionBinding, loggerFactory);
                    try
                    {
                        foreach (var path in tsExtensionPaths) extensionLoadCoordinator.MarkDiscovered(path);
                        var eagerExtensionPaths = new List<string>();
                        foreach (var path in tsExtensionPaths)
                        {
                            if (await tsHost.ReplayCachedDescriptorAsync(path, extensionBinding, cancellationToken))
                            {
                                extensionLoadCoordinator.MarkDescriptorReplayed(path);
                                backgroundExtensionPaths.Add(path);
                            }
                            else
                            {
                                eagerExtensionPaths.Add(path);
                            }
                        }
                        logger.LogInformation($"phase: TS split — {eagerExtensionPaths.Count} eager, {backgroundExtensionPaths.Count} background");
                        var loadStartedAt = DateTimeOffset.UtcNow;
                        try
                        {
                            if (eagerExtensionPaths.Count > 0)
                            {
                                using var bridgeStartTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                bridgeStartTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                                var bridgeStart = Stopwatch.StartNew();
                                await tsHost.StartAsync(bridgeStartTimeout.Token);
                                logger.LogInformation($"phase: TS bridge started in {bridgeStart.Elapsed.TotalSeconds:F1}s");
                                benchmark?.AddPhase("extensions.ts.bridge.start", bridgeStart.Elapsed);
                            }

                            var batchResult = eagerExtensionPaths.Count == 0
                                ? new PiSharp.TsBridge.Protocol.TsExtensionsLoadResult(true, [])
                                : await LoadManyWithTimeoutAsync(tsHost, eagerExtensionPaths, extensionBinding, extensionLoadCoordinator, logger, cancellationToken);
                            var loadCompletedAt = DateTimeOffset.UtcNow;
                            foreach (var loadResult in batchResult.Results ?? [])
                            {
                                var path = loadResult.ExtensionPath ?? string.Empty;
                                var duration = loadResult.Timings is null
                                    ? loadCompletedAt - loadStartedAt
                                    : TimeSpan.FromMilliseconds(loadResult.Timings.Total);
                                benchmark?.AddTypeScriptExtension(path, duration, TimeSpan.Zero, loadResult.Ok, loadResult.Error, loadResult.Timings);
                                if (loadResult.Ok)
                                    extensionLoadCoordinator.MarkReady(path);
                                else
                                {
                                    extensionLoadCoordinator.MarkFailed(path, loadResult.Error);
                                    extensionDiagnostics.Add(new RuntimeDiagnostic(RuntimeDiagnosticType.Warning, $"TypeScript extension '{path}' failed to load: {loadResult.Error ?? "unknown error"}"));
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            foreach (var path in tsExtensionPaths) benchmark?.AddTypeScriptExtension(path, TimeSpan.Zero, TimeSpan.Zero, success: false, exception.Message);
                            throw;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        foreach (var path in tsExtensionPaths) extensionLoadCoordinator.MarkFailed(path, exception.Message);
                        extensionDiagnostics.Add(new RuntimeDiagnostic(RuntimeDiagnosticType.Warning, $"TypeScript extensions were disabled because startup failed: {exception.Message}"));
                        await tsHost.DisposeAsync();
                        tsHost = null;
                    }
                }
            }
            finally
            {
                if (extensionsStopwatch is not null) benchmark!.AddPhase("extensions.total", extensionsStopwatch.Elapsed);
            }
        }

        var contributedResources = await startupContext.MeasureAsync("resources.discover", async () =>
        {
            var payload = new ExtensionResourcesDiscoverPayload(options.Env.Cwd, "startup");
            var discoverEvent = new ExtensionEvent(ExtensionEventNames.ResourcesDiscover, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), payload);

            foreach (var handler in extensions.HandlersFor(ExtensionEventNames.ResourcesDiscover))
            {
                try { await handler.Value.Handler(discoverEvent, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { /* isolate handler failures */ }
            }

            if (tsHost is not null)
            {
                try { await tsHost.ForwardExtensionEventAsync(discoverEvent, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { /* isolate TS bridge failures */ }
            }

            return discoverEvent.ResourcesDiscoverResult;
        });

        if (contributedResources is not null)
        {
            var hasContributions = contributedResources.SkillPaths.Count > 0
                || contributedResources.PromptPaths.Count > 0
                || contributedResources.ThemePaths.Count > 0;
            if (hasContributions)
            {
                resources = resources with
                {
                    SkillPaths = resources.SkillPaths.Concat(contributedResources.SkillPaths).Distinct(StringComparer.Ordinal).ToArray(),
                    PromptTemplatePaths = resources.PromptTemplatePaths.Concat(contributedResources.PromptPaths).Distinct(StringComparer.Ordinal).ToArray(),
                    ThemePaths = resources.ThemePaths.Concat(contributedResources.ThemePaths).Distinct(StringComparer.Ordinal).ToArray()
                };
            }

            if (contributedResources.PromptPaths.Count > 0)
            {
                var (newCatalog, newPromptDiagnostics) = await startupContext.MeasureAsync("prompt-templates.discover.rebuild",
                    () => PromptTemplateCatalog.LoadAsync(options.Env, resources.PromptTemplatePaths, cancellationToken));
                promptTemplateCatalog = newCatalog;
                extensionDiagnostics.AddRange(newPromptDiagnostics.Select(diagnostic =>
                    new RuntimeDiagnostic(RuntimeDiagnosticType.Warning, $"Prompt template {diagnostic.Code} at {diagnostic.Path}: {diagnostic.Message}")));
            }

            if (contributedResources.ThemePaths.Count > 0)
            {
                var (newTheme, newThemeDiagnostics) = await startupContext.MeasureAsync("theme.discover.rebuild",
                    () => TuiThemeDocument.LoadFirstAsync(resources.ThemePaths, cancellationToken));
                if (newTheme is not null) themeDocument = newTheme;
                extensionDiagnostics.AddRange(newThemeDiagnostics.Select(diagnostic =>
                    new RuntimeDiagnostic(RuntimeDiagnosticType.Warning, $"Theme {diagnostic.Code} at {diagnostic.Path}: {diagnostic.Message}")));
            }
        }

        var flagDiagnostics = extensionDiagnostics.Concat(
            startupContext.Measure("extensions.flags.apply", () => ApplyExtensionFlagValues(options.Extensions?.FlagValues ?? new Dictionary<string, object?>(), extensionBinding, extensions.Flags.Select(flag => flag.Value))))
            .ToArray();
        var modelOptions = options.Model ?? new RuntimeModelOptions();
        var sessionThinking = await startupContext.MeasureAsync("session.thinking.resolve", () => ResolveSessionThinkingAsync(session, cancellationToken));
        var defaultThinking = ParseThinkingLevel(settings.Settings.DefaultThinking);
        var requestedThinking = modelOptions.Thinking ?? sessionThinking ?? defaultThinking;
        var selection = startupContext.Measure("model.resolve", () => RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(modelOptions.Provider ?? settings.Settings.DefaultProvider, modelOptions.Model ?? settings.Settings.DefaultModel, requestedThinking, modelOptions.ScopedModels)));
        var registeredTools = tools.Tools.Concat(extensions.Tools.Select(tool => tool.Value)).ToArray();
        var selectedToolNames = startupActiveToolNames ?? registeredTools.Select(tool => tool.Name).ToArray();
        var promptOptions = options.Prompt ?? new RuntimePromptOptions();
        var customPrompt = await startupContext.MeasureAsync("prompt.system.resolve", () => ResolvePromptInputAsync(options.Env, promptOptions.SystemPrompt, resources.SystemPrompt, cancellationToken));
        var appendPrompt = await startupContext.MeasureAsync("prompt.append.resolve", () => ResolveAppendPromptAsync(options.Env, promptOptions.AppendSystemPrompt, resources.AppendSystemPrompts, cancellationToken));
        var systemPromptOptions = new SystemPromptBuildOptions(
            Cwd: options.Env.Cwd,
            Tools: registeredTools.Select(tool => new ToolPromptInfo(tool.Name, ToolPromptSnippet(tool), tool.PromptGuidelines)).ToArray(),
            SelectedToolNames: selectedToolNames,
            CustomPrompt: customPrompt,
            AppendPrompt: appendPrompt,
            ContextFiles: resourcesOptions.DisableContextFiles ? [] : ToSystemPromptContextFiles(resources.ContextFiles),
            ReadmePath: Path.GetFullPath("README.md", options.Env.Cwd),
            DocsPath: Path.GetFullPath("docs", options.Env.Cwd),
            ExamplesPath: Path.GetFullPath("examples", options.Env.Cwd));
        var loadedSkills = await startupContext.MeasureAsync("skills.load", () => LoadSkillsAsync(options.Env, resources.SkillPaths, loggerFactory, cancellationToken));
        var promptOptionsWithSkills = systemPromptOptions with { Skills = loadedSkills };

        var systemPromptContext = SystemPromptBuildOptionsMapper.ToContext(promptOptionsWithSkills);
        Func<ISystemPromptComposer> systemPromptComposerFactory = () =>
        {
            var extensionPromptContributors = extensions.PromptContributors.Select(registration => registration.Value)
                .Concat(extensions.PromptSections.Select(registration => new StaticPromptSectionContributor(
                    registration.Value,
                    new PiSharp.Agent.Core.Prompting.PromptContributionSource(registration.SourceId, PiSharp.Agent.Core.Prompting.PromptContributionSourceKind.Extension))))
                .ToArray();
            return SystemPromptComposer.CreateDefault(extensionPromptContributors, extensions.PromptTransforms.Select(registration => registration.Value));
        };

        AgentHarness<JsonlSessionMetadata> Factory(ISession<JsonlSessionMetadata> currentSession) => new(new AgentHarnessOptions<JsonlSessionMetadata>(
            currentSession,
            selection.Model,
            PublicApi.StreamAsync,
            PublicApi.CompleteAsync,
            registeredTools,
            startupActiveToolNames,
            promptOptionsWithSkills,
            SystemPromptContext: systemPromptContext,
            SystemPromptComposerFactory: systemPromptComposerFactory,
            Skills: loadedSkills.ToArray(),
            ThinkingLevel: selection.ThinkingLevel,
            Extensions: extensions),
            loggerFactory);

        var runtime = startupContext.Measure("runtime.compose", () =>
            new SessionRuntime(repo, createOptions, Factory, session, manager, pluginHost, tsHost, settingsStore, settings, selection, resources, promptOptionsWithSkills, loadedSkills, extensionBinding, flagDiagnostics, promptTemplateCatalog, themeDocument, benchmark?.Build(), extensionLoadCoordinator, loggerFactory, tools: registeredTools, authStorage: authStorage, telemetry: options.Telemetry)
            {
                CachedBackgroundExtensionPaths = backgroundExtensionPaths.ToArray()
            });
        runtime.BindExtensionRuntime();

        // F5: fail loud if any core binding capability is still on its no-op default — the host
        // (this bootstrap) guarantees ExecutionEnv (wrapped by GatedExecutionEnv above) and the
        // binder wires SendMessageAsync / ExecuteToolByNameAsync. Doing this here, not inside
        // BindRuntimeActions, keeps direct-SessionRuntime consumers (tests/embeds) working.
        extensionBinding.BindingsComplete();
        runtime.BindHarnessEventForwarding();
        if (options.Telemetry is not null) runtime.BindTelemetryInstrumentation();
        await runtime.Harness.DispatchSessionStartAsync("startup", cancellationToken);
        if (options.Extensions?.DeferCachedActivationUntilUiReady != true)
        {
            _ = runtime.StartCachedExtensionBackgroundActivationAsync(cancellationToken);
        }
        logger.LogInformation($"phase: startup complete — ext states: {extensionLoadCoordinator.Statuses.Count(s => s.State == ExtensionLoadState.Ready)} ready, {extensionLoadCoordinator.Statuses.Count(s => s.State == ExtensionLoadState.Failed)} failed, {extensionLoadCoordinator.Statuses.Count(s => s.State is not ExtensionLoadState.Ready and not ExtensionLoadState.Failed)} pending");
        bootstrapStopwatch.Stop();
        logger.LogInformation($"bootstrap: create-session complete — elapsedMs={bootstrapStopwatch.ElapsedMilliseconds}, ready={extensionLoadCoordinator.Statuses.Count(s => s.State == ExtensionLoadState.Ready)}, failed={extensionLoadCoordinator.Statuses.Count(s => s.State == ExtensionLoadState.Failed)}, pending={extensionLoadCoordinator.Statuses.Count(s => s.State is not ExtensionLoadState.Ready and not ExtensionLoadState.Failed)}");
        return runtime;
    }

    public static async Task LoadExtensionsIntoAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        if (runtime.ExtensionManager is null || runtime.PluginHost is null || runtime.Resources is null) return;

        foreach (var native in runtime.PluginHost.Discover())
        {
            var plugin = runtime.PluginHost.Load(native);
            await runtime.ExtensionManager.InitializeAsync(plugin.Descriptor, plugin.Extension, runtime.ExtensionBinding, cancellationToken);
        }

        var tsExtensionPaths = runtime.Resources.ExtensionPaths.Where(path => !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (runtime.TsHost is not null && tsExtensionPaths.Length > 0)
        {
            foreach (var path in tsExtensionPaths)
            {
                runtime.ExtensionLoadCoordinator.MarkDiscovered(path);
                runtime.ExtensionLoadCoordinator.MarkPending(path);
            }

            await runtime.TsHost.ResetExtensionsAsync(cancellationToken);
            var result = await runtime.TsHost.LoadManyAsync(tsExtensionPaths, runtime.ExtensionBinding, cancellationToken);
            var byPath = (result.Results ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.ExtensionPath))
                .ToDictionary(item => item.ExtensionPath!, item => item, StringComparer.Ordinal);

            foreach (var path in tsExtensionPaths)
            {
                if (!byPath.TryGetValue(path, out var loadResult))
                {
                    runtime.ExtensionLoadCoordinator.MarkFailed(path, "Extension result missing from TypeScript host response.");
                    continue;
                }

                if (loadResult.Ok) runtime.ExtensionLoadCoordinator.MarkReady(path);
                else runtime.ExtensionLoadCoordinator.MarkFailed(path, loadResult.Error);
            }
        }
    }

    private static string? ToolPromptSnippet(PiSharp.Agent.Core.Tools.IAgentTool tool)
        => string.IsNullOrWhiteSpace(tool.PromptSnippet) ? tool.Description : tool.PromptSnippet;

    private static void EnsureProvidersRegistered(HttpClient? httpClient, IProviderCredentialResolver credentialResolver, string? modelsPath)
    {
        lock (ProviderRegistrationGate)
        {
            if (_providersRegistered) return;
            PublicApi.RegisterBuiltInProviders(httpClient, credentialResolver);
            PublicApi.LoadModelsJson(modelsPath);
            _providersRegistered = true;
        }
    }

    private static IReadOnlyList<RuntimeDiagnostic> ApplyExtensionFlagValues(IReadOnlyDictionary<string, object?> unknownFlags, ExtensionRuntimeBinding binding, IEnumerable<ExtensionFlagRegistration> registrations)
    {
        foreach (var registration in registrations) binding.RegisterFlag(registration);
        var diagnostics = new List<RuntimeDiagnostic>();
        foreach (var pair in unknownFlags)
        {
            if (!binding.TrySetFlagValue(pair.Key, pair.Value, out var error)) diagnostics.Add(new RuntimeDiagnostic(RuntimeDiagnosticType.Error, error!));
        }
        return diagnostics;
    }

    private static IReadOnlyList<SystemPromptContextFile> ToSystemPromptContextFiles(IReadOnlyList<PiResourceContextFile>? contextFiles)
        => contextFiles?.Select(file => new SystemPromptContextFile(file.Path, file.Content)).ToArray() ?? [];

    private static async Task<IReadOnlyList<Skill>> LoadSkillsAsync(IExecutionEnv env, IReadOnlyList<string> skillPaths, ILoggerFactory? loggerFactory, CancellationToken cancellationToken)
    {
        var loaded = new List<Skill>();
        foreach (var skillPath in skillPaths)
        {
            var includeDirectMarkdownFiles = !skillPath.Replace('\\', '/').Contains("/.agents/skills", StringComparison.OrdinalIgnoreCase);
            var (skills, _) = await SkillManager.LoadAsync(env, skillPath, includeDirectMarkdownFiles, cancellationToken, loggerFactory);
            loaded.AddRange(skills);
        }
        return loaded;
    }

    private static async Task<string?> ResolvePromptInputAsync(IExecutionEnv env, string? input, string? fallback, CancellationToken cancellationToken)
    {
        if (input is null) return fallback;
        var absolute = await env.AbsolutePathAsync(input, cancellationToken);
        if (!absolute.IsOk) return input;
        var exists = await env.ExistsAsync(absolute.Value, cancellationToken);
        if (!exists.IsOk || !exists.Value) return input;
        var read = await env.ReadTextFileAsync(absolute.Value, cancellationToken);
        return read.IsOk ? read.Value : input;
    }

    private static async Task<string?> ResolveAppendPromptAsync(IExecutionEnv env, IReadOnlyList<string>? runtimeInputs, IReadOnlyList<string>? discoveredPrompts, CancellationToken cancellationToken)
    {
        if (runtimeInputs is { Count: > 0 })
        {
            var prompts = new List<string>();
            foreach (var input in runtimeInputs)
            {
                var prompt = await ResolvePromptInputAsync(env, input, null, cancellationToken);
                if (!string.IsNullOrWhiteSpace(prompt)) prompts.Add(prompt);
            }
            return prompts.Count == 0 ? null : string.Join("\n\n", prompts);
        }

        var discovered = discoveredPrompts?.Where(prompt => !string.IsNullOrWhiteSpace(prompt)).ToArray() ?? [];
        return discovered.Length == 0 ? null : string.Join("\n\n", discovered);
    }

    private static ThinkingLevel? ParseThinkingLevel(string? value)
        => Enum.TryParse<ThinkingLevel>(value, ignoreCase: true, out var level) ? level : null;

    private static async Task<ThinkingLevel?> ResolveSessionThinkingAsync(ISession<JsonlSessionMetadata> session, CancellationToken cancellationToken)
    {
        var branch = await session.GetBranchAsync(cancellationToken: cancellationToken);
        return branch.OfType<ThinkingLevelChangeEntry>().LastOrDefault() is { } entry
            ? ParseThinkingLevel(entry.ThinkingLevel)
            : null;
    }

    private static async Task<ISession<JsonlSessionMetadata>> ResolveSessionAsync(JsonlSessionRepo repo, JsonlSessionCreateOptions createOptions, RuntimeSessionStartupOptions sessionOptions, CancellationToken cancellationToken)
    {
        if (sessionOptions.NoSession)
        {
            var metadata = new JsonlSessionMetadata(Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow, createOptions.Cwd, "memory://session");
            return new Session<JsonlSessionMetadata>(new MemorySessionStorage<JsonlSessionMetadata>(metadata));
        }

        if (sessionOptions.Fork is not null)
        {
            var sessions = await repo.ListAsync(new JsonlSessionListOptions(createOptions.Cwd), cancellationToken);
            var source = FindSession(sessions, sessionOptions.Fork.SourceSessionIdOrPath ?? sessionOptions.SessionIdOrPath) ?? sessions.FirstOrDefault() ?? throw new InvalidOperationException("No session is available to fork.");
            var forkCreateOptions = string.IsNullOrWhiteSpace(sessionOptions.Fork.NewSessionId) ? createOptions : createOptions with { Id = sessionOptions.Fork.NewSessionId };
            return await repo.ForkAsync(source, forkCreateOptions, new SessionForkOptions(sessionOptions.Fork.EntryId, sessionOptions.Fork.Position, sessionOptions.Fork.NewSessionId), cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(sessionOptions.SessionIdOrPath))
        {
            var sessions = await repo.ListAsync(null, cancellationToken);
            return await repo.OpenAsync(FindSession(sessions, sessionOptions.SessionIdOrPath) ?? throw new InvalidOperationException($"Session '{sessionOptions.SessionIdOrPath}' not found."), cancellationToken);
        }

        if (sessionOptions.ContinueLatestForCwd)
        {
            var sessions = await repo.ListAsync(new JsonlSessionListOptions(createOptions.Cwd), cancellationToken);
            if (sessions.Count > 0) return await repo.OpenAsync(sessions[0], cancellationToken);
        }

        return await repo.CreateAsync(createOptions, cancellationToken);
    }

    private static JsonlSessionMetadata? FindSession(IEnumerable<JsonlSessionMetadata> sessions, string? idOrPath)
        => string.IsNullOrWhiteSpace(idOrPath) ? null : sessions.FirstOrDefault(s => s.Id == idOrPath || s.Path == idOrPath);

    private static async Task<PiSharp.TsBridge.Protocol.TsExtensionsLoadResult> LoadManyWithTimeoutAsync(
        TsExtensionHost tsHost,
        IReadOnlyList<string> paths,
        ExtensionRuntimeBinding binding,
        ExtensionLoadCoordinator coordinator,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(ExtensionLoadTimeoutMinutes));
        try
        {
            logger.LogInformation($"bridge: load_extensions RPC start ({paths.Count} paths, {ExtensionLoadTimeoutMinutes}min timeout)");
            var result = await tsHost.LoadManyAsync(paths, binding, timeoutCts.Token);
            var okCount = result.Results?.Count(r => r.Ok) ?? 0;
            var failCount = (result.Results?.Count ?? 0) - okCount;
            logger.LogInformation($"bridge: load_extensions RPC complete ({okCount} ok, {failCount} failed, {result.Results?.Count ?? 0} total)");
            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation($"bridge: load_extensions RPC TIMEOUT after {ExtensionLoadTimeoutMinutes}min — marking pending as failed");
            var completed = new HashSet<string>((coordinator.Statuses
                .Where(s => s.State is ExtensionLoadState.Ready or ExtensionLoadState.Failed)
                .Select(s => s.ExtensionPath)), StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (!completed.Contains(path))
                    coordinator.MarkFailed(path, $"Extension load timed out after {ExtensionLoadTimeoutMinutes} minutes.");
            }
            var timedOutResults = paths.Select(path =>
                completed.Contains(path)
                    ? new PiSharp.TsBridge.Protocol.TsExtensionLoadResult(true, path)
                    : new PiSharp.TsBridge.Protocol.TsExtensionLoadResult(false, path, $"Extension load timed out after {ExtensionLoadTimeoutMinutes} minutes."))
                .ToArray();
            return new PiSharp.TsBridge.Protocol.TsExtensionsLoadResult(false, timedOutResults);
        }
    }

    private static void RegisterBuiltInOAuthProviders()
    {
        try
        {
            OAuthProviderRegistry.Register(new AnthropicOAuthProvider());
            OAuthProviderRegistry.Register(new GitHubCopilotOAuthProvider());
            OAuthProviderRegistry.Register(new OpenAICodexOAuthProvider());
        }
        catch
        {
            // Non-fatal: OAuth providers are best-effort
        }
    }
}
