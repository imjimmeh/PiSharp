using System.Text.Json;
using PiSharp.Cli.IO;
using PiSharp.Cli.Parsing;
using PiSharp.Client;
using PiSharp.Server.Contracts;
using PiSharp.Server.Serialization;
using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Sessions;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Models;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Files;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Runtime;
using PiSharp.Compatibility.Settings;
using PiSharp.Runtime;
using PiSharp.TsBridge.Protocol;
using PiSharp.Tui.Interactive;
using System.Runtime.CompilerServices;

namespace PiSharp.Cli.Modes;

public static class InteractiveMode
{
    private static ILogger? _footerLogger;

    public static async Task<int> RunAsync(SessionRuntime runtime, CancellationToken cancellationToken = default, bool local = false)
    {
        var options = CreateTuiHostOptions(runtime);
        var host = new TuiHost(options);
        return await host.RunAsync(cancellationToken);
    }

    internal static TuiHostOptions CreateTuiHostOptions(SessionRuntime runtime, TuiFooterSnapshotProvider? footerSnapshotProvider = null)
    {
        _footerLogger = runtime.LoggerFactory?.CreateLogger(nameof(InteractiveMode));
        footerSnapshotProvider ??= new TuiFooterSnapshotProvider(loggerFactory: runtime.LoggerFactory);

        async Task<TuiCommandDispatchResult> DispatchCommandAsync(TuiCommandDispatchRequest request, CancellationToken token)
        {
            var name = request.Text.Trim()[1..].Split([' '], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var logger = runtime.LoggerFactory?.CreateLogger(nameof(InteractiveMode));
            logger?.LogDebug("Slash command dispatch starting text={CommandText} name={CommandName}", request.Text, name);
            var context = new SlashCommandContext(
                name,
                runtime,
                request.SelectAsync,
                request.InputAsync,
                (message, ct) => request.NotifyAsync(message, false, ct),
                OAuthStorage: new FileOAuthStorage(PiAgentPaths.FromCwd(runtime.Session.Metadata.Cwd).AuthPath),
                SelectSessionMetadataAsync: request.SelectSessionMetadataAsync,
                OpenUrlAsync: OAuthBrowserLauncher.OpenAsync);
            var result = await BuildCommandRegistry(runtime).ExecuteAsync(request.Text, context, token);
            logger?.LogDebug("Slash command dispatch finished text={CommandText} name={CommandName} handled={Handled} shouldExit={ShouldExit} isError={IsError} hasMessage={HasMessage}",
                request.Text, name, result.Handled, result.ShouldExit, result.IsError, result.Message is not null);
            if (result.Message is not null) await request.NotifyAsync(result.Message, result.IsError, token);
            return new TuiCommandDispatchResult(result.Handled, result.ShouldExit);
        }

        PiSharp.Agent.Core.Tools.IAgentTool? ResolveExtensionTool(string name)
        {
            var manager = runtime.ExtensionManager;
            if (manager is null) return null;
            var registration = manager.Registry.Tools.FirstOrDefault(tool => string.Equals(tool.Value.Name, name, StringComparison.Ordinal));
            return registration?.Value;
        }

        async Task CycleThinkingLevelAsync(CancellationToken token)
        {
            var logger = runtime.LoggerFactory?.CreateLogger(nameof(InteractiveMode));
            var model = runtime.Harness.Model;
            var harnessId = RuntimeHelpers.GetHashCode(runtime.Harness);
            var supportedLevels = ModelRegistry.GetSupportedThinkingLevels(model);
            var nextThinking = RuntimeModelSelector.CycleThinking(runtime.Harness.Model, runtime.Harness.ThinkingLevel, +1);
            logger?.LogDebug(
                "Thinking level cycle requested harnessId={HarnessId} model={Provider}/{ModelId} currentLevel={CurrentLevel} nextLevel={NextLevel} supportedLevels={SupportedLevels}",
                harnessId,
                model.Provider,
                model.Id,
                runtime.Harness.ThinkingLevel,
                nextThinking,
                string.Join(",", supportedLevels));
            await runtime.SetThinkingLevelAsync(nextThinking, token);
            logger?.LogDebug(
                "Thinking level cycle applied harnessId={HarnessId} harnessThinking={HarnessThinking} selectionThinking={SelectionThinking}",
                harnessId,
                runtime.Harness.ThinkingLevel,
                runtime.CurrentModelSelection.ThinkingLevel);
            await runtime.PersistCurrentModelSelectionAsync(token);
            logger?.LogDebug(
                "Thinking level cycle persisted harnessId={HarnessId} selectionThinking={SelectionThinking}",
                harnessId,
                runtime.CurrentModelSelection.ThinkingLevel);
        }

        TuiFooterSnapshot CreateFooterSnapshot(TuiRenderState state)
        {
            try
            {
                var sessionEntries = state.SessionBranchEntries ?? runtime.Session.GetBranchAsync().GetAwaiter().GetResult();
                return footerSnapshotProvider.CreateSnapshotFromSessionEntries(state, runtime.Session.Metadata.Cwd, sessionEntries);
            }
            catch (Exception ex)
            {
                _footerLogger?.LogDebug(ex, "Branch metadata snapshot failed, using static footer");
                return footerSnapshotProvider.CreateSnapshot(state, runtime.Session.Metadata.Cwd);
            }
        }

        async Task<TuiSessionSnapshot> GetSessionSnapshotAsync(CancellationToken token)
        {
            var metadata = runtime.Session.Metadata;
            return new TuiSessionSnapshot(
                metadata.Id,
                metadata.Path,
                await runtime.Session.GetSessionNameAsync(token),
                await runtime.Session.GetBranchAsync(cancellationToken: token));
        }

        async Task ForkFromEntryAsync(string entryId, CancellationToken token)
            => await runtime.ForkAsync(runtime.Session.Metadata, new SessionForkOptions(entryId, "at"), token);

        return new TuiHostOptions(
            new InProcessTuiFacade(runtime),
            runtime.Session.Metadata.Id,
            runtime.Session.Metadata.Path,
            token => runtime.Session.GetSessionNameAsync(token),
            DispatchCommandAsync,
            text => BuildCommandRegistry(runtime).Complete(text),
            runtime.Session.Metadata.Cwd,
            FooterSnapshot: CreateFooterSnapshot,
            ConfigureUiBridge: bridge =>
            {
                runtime.ExtensionBinding.SetUi(new TuiExtensionUi(bridge), true);
                if (runtime.TsHost is not null)
                {
                    bridge.SendCustomUiInputAsync = async (requestId, data, width, height, eventName, token) =>
                    {
                        var snapshot = await runtime.TsHost.SendCustomUiInputAsync(new TsCustomUiInputRequest(requestId, data, width, height, eventName), token);
                        return new ExtensionCustomUiSnapshot(snapshot.RequestId, snapshot.Lines, snapshot.Width, snapshot.Height, snapshot.Completed, snapshot.Value, snapshot.Error);
                    };

                    async Task ConfigureTsUiAsync()
                    {
                        try
                        {
                            await runtime.TsHost.SetRuntimeHasUiAsync(true);
                            await runtime.StartCachedExtensionBackgroundActivationAsync();
                        }
                        catch (Exception exception)
                        {
                            runtime.LoggerFactory?.CreateLogger(nameof(InteractiveMode)).LogWarning(exception, "TypeScript UI extension activation failed during TUI startup");
                        }
                    }

                    runtime.TsHost.SetUiBridge(async request =>
                    {
                        runtime.LoggerFactory?.CreateLogger(nameof(InteractiveMode)).LogDebug(
                            "TypeScript UI request received requestId={RequestId} extensionId={ExtensionId} kind={Kind} title={Title}",
                            request.RequestId, request.ExtensionId, request.Kind, request.Title);
                        var result = await bridge.HandleAsync(new ExtensionUiIntent(request.RequestId, request.Kind, request.Title, request.Message, request.Options, request.Component, request.ExtensionId));
                        runtime.LoggerFactory?.CreateLogger(nameof(InteractiveMode)).LogDebug(
                            "TypeScript UI request completed requestId={RequestId} extensionId={ExtensionId} kind={Kind} cancelled={Cancelled}",
                            request.RequestId, request.ExtensionId, request.Kind, result.Cancelled);
                        return new TsUiResponse(result.RequestId, result.Value, result.Cancelled);
                    });

                    _ = Task.Run(ConfigureTsUiAsync);
                }
            },
            StartupMessages: StartupResourceSummary.Create(runtime),
            PostStartupChecksAsync: async (inject, token) =>
            {
                var offline = string.Equals(
                        Environment.GetEnvironmentVariable("PI_OFFLINE"), "1", StringComparison.Ordinal)
                    || runtime.SettingsSnapshot?.Settings.Offline == true;
                if (offline) return;

                var packages = runtime.Resources?.Packages ?? [];
                var checker = new NpmOutdatedChecker(new NpmRegistryClient(new HttpClient()));
                var outdated = await checker.CheckAsync(packages, token);
                var message = OutdatedPackagesSummary.Format(outdated);
                if (message is not null) await inject(message);
            },
            Theme: runtime.Theme,
            GetExtensionShortcuts: () => runtime.ExtensionManager?.Registry.Shortcuts ?? [],
            GetExtensionRegistry: () => runtime.ExtensionManager?.Registry,
            ResolveTool: ResolveExtensionTool,
            CycleThinkingLevelAsync: CycleThinkingLevelAsync,
            ProcessFileReferencesAsync: async (text, cwd, token) =>
            {
                var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync(text, cwd, token);
                return (processed.Text, processed.Images);
            },
            ProcessInputAsync: async (text, images, source, token) =>
            {
                if (text.StartsWith("!", StringComparison.Ordinal) && text.Length > 1)
                {
                    var excludeFromContext = text.StartsWith("!!", StringComparison.Ordinal);
                    var command = excludeFromContext ? text[2..].Trim() : text[1..].Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        var bashResult = await runtime.DispatchUserBashAsync(command, excludeFromContext, token).ConfigureAwait(false);
                        if (bashResult?.Result is { } bashExecutionResult)
                        {
                            var output = bashExecutionResult.Error.Length > 0
                                ? $"{bashExecutionResult.Output}\n\n{bashExecutionResult.Error}"
                                : bashExecutionResult.Output;
                            return new TuiInputHookResult(true, output, null);
                        }
                    }
                }
                var result = await runtime.DispatchInputAsync(text, images, source, token).ConfigureAwait(false);
                return new TuiInputHookResult(result.Handled, result.Text, result.Images);
            },
            GetSessionSnapshotAsync: GetSessionSnapshotAsync,
            ForkFromEntryAsync: ForkFromEntryAsync,
            GetExtensionLoadStatus: () =>
            {
                var summary = runtime.GetExtensionLoadSummary();
                var failures = summary.FailedDiagnostics
                    .Select(failure => new TuiExtensionLoadFailure(failure.Path, failure.Diagnostic))
                    .ToArray();
                return new TuiExtensionLoadStatus(summary.Total, summary.Active, summary.BlockingActive, summary.Ready, summary.Failed, failures);
            },
            ExtensionLoadCommandWhitelist: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/quit" },
            LoggerFactory: runtime.LoggerFactory);
    }

    /// <summary>
    /// Selects the daemon lease for remote interactive mode: returns the stored lease when it is
    /// still healthy, otherwise auto-starts a daemon and returns its fresh lease (null when the
    /// daemon could not be started). Both checks are injectable so tests can avoid real daemons.
    /// </summary>
    internal static async Task<DaemonLease?> SelectLeaseAsync(
        DaemonLeaseStore store,
        Func<DaemonLease, CancellationToken, Task<bool>>? isHealthy = null,
        Func<CancellationToken, Task<DaemonLease?>>? autoStart = null,
        CancellationToken ct = default)
    {
        isHealthy ??= DaemonDiscovery.IsDaemonAvailableAsync;
        autoStart ??= token => DaemonMode.TryAutoStartAsync(store, token);

        var lease = await store.ReadAsync(ct);
        if (lease is not null && await isHealthy(lease, ct)) return lease;

        return await autoStart(ct);
    }

    /// <summary>
    /// Runs the TUI against a daemon-hosted session over the wire. The daemon session stays live
    /// after this client exits so a later client can re-attach (Task4.3).
    /// </summary>
    internal static async Task<int> RunRemoteAsync(DaemonLease lease, CliArgs runtimeArgs, IConsoleIO console, CancellationToken cancellationToken)
    {
        await using var transport = new ClientWebSocketTransport();
        await using var connection = new ClientSessionConnection(transport);
        await using var backend = new RemoteTuiBackend(connection);

        await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{lease.Port}"), lease.ApiKey, cancellationToken);

        var created = await CreateRemoteSessionAsync(connection, runtimeArgs, cancellationToken);
        var sessionId = created.ServerSessionId;
        backend.ServerSessionId = sessionId;

        var startupMessages = await backend.GetStartupMessagesAsync(cancellationToken);
        var theme = await backend.GetThemeAsync(cancellationToken);

        var options = new TuiHostOptions(
            backend,
            sessionId,
            SessionFile: null,
            backend.GetSessionNameAsync,
            DispatchCommandAsync: backend.DispatchCommandAsync,
            CompleteCommand: text => backend.CompleteCommandAsync(text, CancellationToken.None).GetAwaiter().GetResult(),
            WorkingDirectory: Directory.GetCurrentDirectory(),
            FooterSnapshot: null,
            ConfigureUiBridge: bridge =>
            {
                backend.UiRequestHandler = async (intent, ct) =>
                {
                    var result = await bridge.HandleAsync(
                        new ExtensionUiIntent(intent.RequestId, intent.Kind, intent.Title, intent.Message, intent.Options, intent.Component, intent.ExtensionId),
                        ct);
                    return new ServerUiResponse(result.RequestId, result.Value, result.Cancelled);
                };
            },
            StartupMessages: startupMessages,
            PostStartupChecksAsync: (inject, ct) => backend.PostStartupChecksAsync(inject, ct),
            Theme: theme,
            GetExtensionShortcuts: () => backend.GetExtensionShortcutsAsync(CancellationToken.None).GetAwaiter().GetResult(),
            GetExtensionRegistry: () => null,
            ResolveTool: _ => null,
            CycleThinkingLevelAsync: backend.CycleThinkingLevelAsync,
            ProcessFileReferencesAsync: async (text, cwd, token) =>
            {
                var processed = await FileReferenceProcessor.ProcessInlineReferencesAsync(text, cwd, token);
                return (processed.Text, processed.Images);
            },
            ProcessInputAsync: backend.ProcessInputAsync,
            GetSessionSnapshotAsync: backend.GetSessionSnapshotAsync,
            ForkFromEntryAsync: backend.ForkFromEntryAsync,
            GetExtensionLoadStatus: () => backend.GetExtensionLoadStatusAsync(CancellationToken.None).GetAwaiter().GetResult(),
            ExtensionLoadCommandWhitelist: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/quit" },
            LoggerFactory: null);

        var host = new TuiHost(options);
        var exit = await host.RunAsync(cancellationToken);

        await backend.DisposeAsync();
        return exit;
    }

    private static async Task<ServerSessionCreated> CreateRemoteSessionAsync(ClientSessionConnection connection, CliArgs runtimeArgs, CancellationToken cancellationToken)
    {
        var request = new CreateServerSessionRequest(
            Cwd: Directory.GetCurrentDirectory(),
            Provider: runtimeArgs.Provider,
            Model: runtimeArgs.Model,
            Thinking: runtimeArgs.Thinking,
            ScopedModels: runtimeArgs.Models,
            Tools: runtimeArgs.Tools,
            NoTools: runtimeArgs.NoTools,
            NoBuiltinTools: runtimeArgs.NoBuiltinTools,
            Extensions: runtimeArgs.Extensions,
            NoExtensions: runtimeArgs.NoExtensions,
            NoSkills: runtimeArgs.NoSkills,
            NoPromptTemplates: runtimeArgs.NoPromptTemplates,
            NoThemes: runtimeArgs.NoThemes,
            NoContextFiles: runtimeArgs.NoContextFiles);

        var response = await connection.SendAsync(new ServerCommandEnvelope(ServerCommandTypes.CreateSession), request, cancellationToken);
        if (!response.Success)
        {
            throw new InvalidOperationException($"create_session failed: {response.Error?.Code}: {response.Error?.Message}");
        }

        return FromPayload<ServerSessionCreated>(response.Data)
            ?? throw new InvalidOperationException("create_session returned no session.");
    }

    /// <summary>
    /// Round-trips the opaque command <see cref="ServerResponse.Data"/> (a <see cref="JsonElement"/>
    /// on the wire, or an anonymous object in-process) into a typed record using the server JSON
    /// options — the same approach as <c>ClientToTuiAdapter.FromPayload</c>, which is internal to
    /// PiSharp.Client and not visible here.
    /// </summary>
    private static T? FromPayload<T>(object? data)
        => data is null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(data, ServerJsonSerializer.Options), ServerJsonSerializer.Options);

    private static SlashCommandRegistry BuildCommandRegistry(SessionRuntime runtime)
        => SlashCommandRegistryFactory.Create(runtime);
}
