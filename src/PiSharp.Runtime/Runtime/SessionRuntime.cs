using System.Text.Json;
using System.Runtime.CompilerServices;
using PiSharp.Ai.Auth;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Resources;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Compatibility.Resources;
using PiSharp.Compatibility.Settings;
using PiSharp.Extensions;
using PiSharp.PluginHost;
using PiSharp.TsBridge;
using PiSharp.Runtime.Telemetry;

namespace PiSharp.Runtime;

public sealed class SessionRuntime(
    ISessionRepo<JsonlSessionMetadata, JsonlSessionCreateOptions, JsonlSessionListOptions> repo,
    JsonlSessionCreateOptions createOptions,
    Func<ISession<JsonlSessionMetadata>, AgentHarness<JsonlSessionMetadata>> harnessFactory,
    ISession<JsonlSessionMetadata> initialSession,
    ExtensionManager? extensionManager = null,
    NativePluginHost? pluginHost = null,
    TsExtensionHost? tsHost = null,
    PiSettingsStore? settingsStore = null,
    PiSettingsSnapshot? settingsSnapshot = null,
    RuntimeModelSelection? currentModelSelection = null,
    PiResources? resources = null,
    SystemPromptBuildOptions? systemPromptOptions = null,
    IReadOnlyList<Skill>? skills = null,
    ExtensionRuntimeBinding? extensionBinding = null,
    IReadOnlyList<RuntimeDiagnostic>? extensionFlagDiagnostics = null,
    PromptTemplateCatalog? promptTemplates = null,
    TuiThemeDocument? theme = null,
    StartupBenchmarkReport? startupBenchmark = null,
    ExtensionLoadCoordinator? extensionLoadCoordinator = null,
    ILoggerFactory? loggerFactory = null,
    IReadOnlyList<PiSharp.Agent.Core.Tools.IAgentTool>? tools = null,
    IOAuthStorage? authStorage = null,
    TelemetryService? telemetry = null) : IAsyncDisposable
{
    private Func<SessionRuntime, CancellationToken, Task>? _rebind;
    private readonly RuntimeEventBridge _eventBridge = new(loggerFactory);
    private IDisposable? _extensionDispatchSubscription;
    private readonly RuntimeSessionController _sessionController = new(repo, createOptions, harnessFactory, extensionManager);
    private readonly ExtensionSettingsService _settingsService = new(settingsStore, settingsSnapshot, extensionManager?.Registry, loggerFactory);
    private readonly ExtensionStateService _stateService = BuildStateService(settingsSnapshot, createOptions.Cwd, loggerFactory);
    private RuntimeModelController? _modelController;
    private readonly RuntimeExtensionReloader _extensionReloader = new();
    private readonly ILogger _logger = loggerFactory?.CreateLogger<SessionRuntime>() ?? NullLogger<SessionRuntime>.Instance;
    private readonly object _backgroundActivationGate = new();
    private RuntimeModelController ModelController => _modelController ??= new(_settingsService, loggerFactory);
    private readonly CancellationTokenSource _backgroundActivationCancellation = new();
    private Task? _backgroundActivationTask;

    internal ExtensionSettingsService SettingsService => _settingsService;
    internal ExtensionStateService StateService => _stateService;
    private static ExtensionRuntimeBinding WireBinding(
        ExtensionRuntimeBinding? provided,
        JsonlSessionCreateOptions opts,
        TelemetryService? telemetry)
    {
        var binding = provided ?? new ExtensionRuntimeBinding(opts.Cwd, false, NoExtensionUi.Instance);
        if (telemetry is not null) binding.Telemetry = telemetry;
        return binding;
    }
    private static ExtensionStateService BuildStateService(PiSettingsSnapshot? snapshot, string cwd, ILoggerFactory? loggerFactory)
    {
        var paths = snapshot is null
            ? PiAgentPaths.FromCwd(cwd)
            : PiAgentPaths.FromCwd(cwd, snapshot.Paths.HomeDirectory, snapshot.Paths.Profile);
        return new ExtensionStateService(
            Path.Combine(paths.GlobalPiSharpDirectory, "extensions"),
            Path.Combine(paths.ProjectPiSharpDirectory, "extensions"),
            loggerFactory);
    }
    public ILoggerFactory? LoggerFactory { get; } = loggerFactory;

    internal ISessionRepo<JsonlSessionMetadata, JsonlSessionCreateOptions, JsonlSessionListOptions> SessionRepo { get; } = repo;
    internal JsonlSessionCreateOptions CreateOptions { get; } = createOptions;
    internal Func<ISession<JsonlSessionMetadata>, AgentHarness<JsonlSessionMetadata>> HarnessFactory { get; } = harnessFactory;

    public ISession<JsonlSessionMetadata> Session { get; private set; } = initialSession;
    public AgentHarness<JsonlSessionMetadata> Harness { get; private set; } = harnessFactory(initialSession);
    public ExtensionManager? ExtensionManager { get; } = extensionManager;
    public ExtensionRuntimeBinding ExtensionBinding { get; } = WireBinding(extensionBinding, createOptions, telemetry);
    public TelemetryService? Telemetry { get; } = telemetry;
    public NativePluginHost? PluginHost { get; } = pluginHost;
    public TsExtensionHost? TsHost { get; } = tsHost;
    public PiResources? Resources { get; } = resources;
    public PiSettingsSnapshot? SettingsSnapshot { get; } = settingsSnapshot;
    public SystemPromptBuildOptions? SystemPromptOptions { get; } = systemPromptOptions;
    public IReadOnlyList<Skill> Skills => Harness.Skills;
    public IReadOnlyList<PiSharp.Agent.Core.Tools.IAgentTool> Tools { get; } = tools ?? [];
    internal IOAuthStorage? AuthStorage { get; } = authStorage;
    public IReadOnlyList<RuntimeDiagnostic> ExtensionFlagDiagnostics { get; } = extensionFlagDiagnostics ?? [];
    public PromptTemplateCatalog PromptTemplates { get; } = promptTemplates ?? PromptTemplateCatalog.Empty;
    public bool AutoCompactionEnabled { get; set; }
    public bool AutoRetryEnabled { get; set; }
    public string SteeringMode { get; set; } = "all";
    public string FollowUpMode { get; set; } = "all";
    public void ResetRetryState() { AutoRetryEnabled = false; }
    public TuiThemeDocument? Theme { get; private set; } = theme;

    /// <summary>
    /// Raised when the active theme document changes (in-process theme authority;
    /// daemon-side theme events land in P01).
    /// </summary>
    public event EventHandler? ThemeChanged;
    public async Task SetThemeByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var paths = Resources?.ThemePaths;
        if (paths is null || paths.Count == 0) return;
        var documents = await TuiThemeDocument.LoadAllAsync(paths, cancellationToken);
        var match = documents.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null || (Theme is not null && string.Equals(Theme.Name, match.Name, StringComparison.OrdinalIgnoreCase))) return;
        Theme = match;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
    public StartupBenchmarkReport? StartupBenchmark { get; } = startupBenchmark;
    public ExtensionLoadCoordinator ExtensionLoadCoordinator { get; } = extensionLoadCoordinator ?? new ExtensionLoadCoordinator();
    public ExtensionLoadSummary GetExtensionLoadSummary() => ExtensionLoadSummary.From(ExtensionLoadCoordinator.Statuses);
    public PromptDebugView? LastPromptDebugView => Harness.LastPromptDocument is null ? null : PromptDebugView.FromDocument(Harness.LastPromptDocument);
    private RuntimeModelSelection? _currentModelSelection = currentModelSelection;
    public RuntimeModelSelection CurrentModelSelection
    {
        get => _currentModelSelection ??= new RuntimeModelSelection(Harness.Model, Harness.ThinkingLevel, [Harness.Model], false);
        private set => _currentModelSelection = value;
    }

    public sealed record RuntimeInputHookResult(bool Handled, string Text, IReadOnlyList<ImageContent>? Images);

    public IReadOnlyList<string> CachedBackgroundExtensionPaths { get; init; } = [];

    public Task StartCachedExtensionBackgroundActivationAsync(CancellationToken cancellationToken = default)
    {
        if (TsHost is null || CachedBackgroundExtensionPaths.Count == 0) return Task.CompletedTask;

        lock (_backgroundActivationGate)
        {
            if (_backgroundActivationTask is not null) return _backgroundActivationTask;

            _logger.LogInformation($"phase: starting cached extension background activation ({CachedBackgroundExtensionPaths.Count} extensions)");
            var backgroundToken = _backgroundActivationCancellation.Token;
            _backgroundActivationTask = TsHost.ActivateExtensionsInBackgroundAsync(CachedBackgroundExtensionPaths, ExtensionBinding, backgroundToken);
            foreach (var path in CachedBackgroundExtensionPaths)
            {
                _ = ExtensionLoadCoordinator.RunInBackgroundAsync(path, token => TsHost.ActivateExtensionInBackgroundAsync(path, ExtensionBinding, token), backgroundToken);
            }

            return _backgroundActivationTask;
        }
    }

    public sealed record RuntimeUserBashHookResult(ExtensionBashOperations? Operations, ExtensionBashResult? Result)
    {
        public string? Command => Result?.Command;
        public int? ExitCode => Result?.ExitCode;
        public string? Output => Result?.Output;
        public string? Error => Result?.Error;
    }

    public sealed record RuntimeSessionChangeResult(bool Cancelled, string? Reason = null, JsonlSessionMetadata? Session = null)
    {
        public static RuntimeSessionChangeResult Applied(JsonlSessionMetadata session) => new(false, null, session);
        public static RuntimeSessionChangeResult CancelledByExtension(string? reason) => new(true, reason);
    }

    public void SetRebindSession(Func<SessionRuntime, CancellationToken, Task> rebind) => _rebind = rebind;

    public void BindHarnessEventForwarding()
        => _eventBridge.BindHarnessEventForwarding(Harness, TsHost, ExtensionManager);

    public void BindTelemetryInstrumentation()
        => _eventBridge.BindTelemetryInstrumentation(Harness, Telemetry);

    public void UnbindHarnessEventForwarding()
        => _eventBridge.UnbindHarnessEventForwarding();

    public void BindExtensionRuntime() => _sessionController.ExtensionBinder.BindRuntimeActions(this);

    public Task SendExtensionMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn, CancellationToken cancellationToken)
    {
        switch (delivery)
        {
            case ExtensionMessageDelivery.Steer: Harness.Steer(message); break;
            case ExtensionMessageDelivery.FollowUp: Harness.FollowUp(message); break;
            case ExtensionMessageDelivery.NextTurn: Harness.QueueNextTurn(message); break;
        }
        return triggerTurn && Harness.Phase == AgentHarnessPhase.Idle
            ? Harness.PromptAsync(string.Empty, cancellationToken)
            : Task.CompletedTask;
    }

    public Task ReloadExtensionsAsync(CancellationToken cancellationToken = default)
        => _extensionReloader.ReloadAsync(this, cancellationToken);

    public async Task SetModelAsync(RuntimeModelSelection selection, string source = "runtime", CancellationToken cancellationToken = default)
        => CurrentModelSelection = await ModelController.SetModelAsync(Harness, selection, source, cancellationToken);

    public async Task SetModelAsync(ModelDescriptor model, string source = "runtime", CancellationToken cancellationToken = default)
        => CurrentModelSelection = await ModelController.SetModelAsync(Harness, CurrentModelSelection, model, source, cancellationToken);

    public async Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default)
    {
        var harnessId = RuntimeHelpers.GetHashCode(Harness);
        var previousSelection = CurrentModelSelection;
        _logger.LogDebug(
            "Runtime thinking level update requested harnessId={HarnessId} currentSelectionThinking={CurrentSelectionThinking} requestedLevel={RequestedLevel}",
            harnessId,
            previousSelection.ThinkingLevel,
            level);
        CurrentModelSelection = await ModelController.SetThinkingLevelAsync(Harness, previousSelection, level, cancellationToken);
        _logger.LogDebug(
            "Runtime thinking level update completed harnessId={HarnessId} previousSelectionThinking={PreviousSelectionThinking} nextSelectionThinking={NextSelectionThinking} harnessThinking={HarnessThinking}",
            harnessId,
            previousSelection.ThinkingLevel,
            CurrentModelSelection.ThinkingLevel,
            Harness.ThinkingLevel);
    }

    public async Task PersistCurrentModelSelectionAsync(CancellationToken cancellationToken = default)
    {
        var harnessId = RuntimeHelpers.GetHashCode(Harness);
        _logger.LogDebug(
            "Runtime persisting current model selection harnessId={HarnessId} selectionThinking={SelectionThinking} model={Provider}/{ModelId}",
            harnessId,
            CurrentModelSelection.ThinkingLevel,
            CurrentModelSelection.Model.Provider,
            CurrentModelSelection.Model.Id);
        await ModelController.PersistPendingSelectionAsync(cancellationToken);
        _logger.LogDebug(
            "Runtime persisted current model selection harnessId={HarnessId} selectionThinking={SelectionThinking} model={Provider}/{ModelId}",
            harnessId,
            CurrentModelSelection.ThinkingLevel,
            CurrentModelSelection.Model.Provider,
            CurrentModelSelection.Model.Id);
    }

    public Task<ExtensionModelSelection?> ResolveRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        var normalizedName = role.StartsWith('@') ? role[1..] : role;
        try
        {
            var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@" + normalizedName, null));
            return Task.FromResult<ExtensionModelSelection?>(new ExtensionModelSelection(selection.Model, selection.ThinkingLevel));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult<ExtensionModelSelection?>(null);
        }
    }

    public async Task<bool> SetModelByRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        var normalizedName = role.StartsWith('@') ? role[1..] : role;
        try
        {
            var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, "@" + normalizedName, null));
            await SetModelAsync(selection, "extension", cancellationToken);
            await PersistCurrentModelSelectionAsync(cancellationToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task ShutdownFromExtensionAsync(CancellationToken cancellationToken = default)
    {
        Harness.Abort();
        await DispatchSessionShutdownAsync("extension", null, cancellationToken);
    }

    public async Task<RuntimeInputHookResult> DispatchInputAsync(string text, IReadOnlyList<ImageContent>? images = null, string source = "runtime", CancellationToken cancellationToken = default)
    {
        if (ExtensionManager is null) return new RuntimeInputHookResult(false, text, images);
        var currentText = text;
        var currentImages = images;
        foreach (var handler in ExtensionManager.Registry.HandlersFor(ExtensionEventNames.Input))
        {
            var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.Input(currentText, currentImages, source));
            var extensionEvent = new ExtensionEvent(ExtensionEventNames.Input, harnessEvent, new ExtensionInputEvent(currentText, currentImages, source));
            await handler.Value.Handler(extensionEvent, cancellationToken).ConfigureAwait(false);
            var inputResult = extensionEvent.InputResult;
            if (inputResult is null) continue;
            if (string.Equals(inputResult.Action, "handled", StringComparison.OrdinalIgnoreCase)) return new RuntimeInputHookResult(true, currentText, currentImages);
            if (string.Equals(inputResult.Action, "transform", StringComparison.OrdinalIgnoreCase) && inputResult.Text is not null)
            {
                currentText = inputResult.Text;
                currentImages = inputResult.Images ?? currentImages;
            }
        }
        return new RuntimeInputHookResult(false, currentText, currentImages);
    }

    public async Task<AssistantMessage?> SubmitPromptAsync(string text, IReadOnlyList<ImageContent>? images = null, string source = "runtime", CancellationToken cancellationToken = default)
    {
        var input = await DispatchInputAsync(text, images, source, cancellationToken).ConfigureAwait(false);
        return input.Handled ? null : await Harness.PromptAsync(input.Text, input.Images, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeSessionChangeResult> NewSessionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new session");
        var decision = await DispatchSessionBeforeSwitchAsync("new", null, cancellationToken);
        if (decision.Cancelled) return decision;
        var replacement = await _sessionController.NewSessionAsync(Harness, cancellationToken);
        await DispatchSessionShutdownAsync("new", replacement.Session.Metadata.Path, cancellationToken);
        await ApplySessionReplacementAsync(replacement, cancellationToken);
        return RuntimeSessionChangeResult.Applied(Session.Metadata);
    }

    public async Task<RuntimeSessionChangeResult> SwitchSessionAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Switching to session {SessionId}", metadata.Id);
        var decision = await DispatchSessionBeforeSwitchAsync("switch", metadata, cancellationToken);
        if (decision.Cancelled) return decision;
        var replacement = await _sessionController.SwitchSessionAsync(metadata, Harness, cancellationToken);
        await DispatchSessionShutdownAsync("switch", replacement.Session.Metadata.Path, cancellationToken);
        await ApplySessionReplacementAsync(replacement, cancellationToken);
        return RuntimeSessionChangeResult.Applied(Session.Metadata);
    }

    public async Task<RuntimeSessionChangeResult> ForkAsync(JsonlSessionMetadata source, SessionForkOptions forkOptions, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Forking session {SessionId}", source.Id);
        var decision = await DispatchSessionBeforeForkAsync(source, forkOptions, cancellationToken);
        if (decision.Cancelled) return decision;
        var replacement = await _sessionController.ForkAsync(source, forkOptions, Harness, cancellationToken);
        await DispatchSessionShutdownAsync("fork", replacement.Session.Metadata.Path, cancellationToken);
        await ApplySessionReplacementAsync(replacement, cancellationToken);
        return RuntimeSessionChangeResult.Applied(Session.Metadata);
    }

    public async Task<RuntimeSessionChangeResult> ImportSessionFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Importing session file {FilePath}", filePath);

        if (!File.Exists(filePath))
            return RuntimeSessionChangeResult.CancelledByExtension("Session file not found.");

        JsonlSessionMetadata imported;
        try
        {
            var headerLine = File.ReadLines(filePath).Take(1).FirstOrDefault();
            if (headerLine is null)
                return RuntimeSessionChangeResult.CancelledByExtension("Empty session file.");
            using var doc = JsonDocument.Parse(headerLine);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Missing session id in header.");
            var cwd = root.GetProperty("cwd").GetString()
                ?? throw new InvalidOperationException("Missing cwd in header.");
            var timestamp = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(ts.GetString()!)
                : DateTimeOffset.UtcNow;

            var sessionsCwdDir = Path.GetDirectoryName(Session.Metadata.Path)
                ?? throw new InvalidOperationException("Cannot determine sessions directory.");
            var destFileName = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fff}_{id}.jsonl";
            var destPath = Path.Combine(sessionsCwdDir, destFileName);

            if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(filePath, destPath, overwrite: true);

            imported = new JsonlSessionMetadata(id, timestamp, cwd, destPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _logger.LogWarning(ex, "Failed to parse session import file {FilePath}", filePath);
            return RuntimeSessionChangeResult.CancelledByExtension($"Invalid session file: {ex.Message}");
        }

        var decision = await DispatchSessionBeforeSwitchAsync("import", imported, cancellationToken);
        if (decision.Cancelled) return decision;
        var replacement = await _sessionController.SwitchSessionAsync(imported, Harness, cancellationToken);
        await DispatchSessionShutdownAsync("import", replacement.Session.Metadata.Path, cancellationToken);
        await ApplySessionReplacementAsync(replacement, cancellationToken);
        return RuntimeSessionChangeResult.Applied(Session.Metadata);
    }

    public async Task<IReadOnlyList<JsonlSessionMetadata>> ListSessionsAsync(string? cwd = null, CancellationToken cancellationToken = default)
        => await _sessionController.ListSessionsAsync(cwd, cancellationToken);

    public async Task<IReadOnlyList<object>> GetForkableEntriesAsync(CancellationToken cancellationToken = default)
    {
        var entries = await Session.GetEntriesAsync(cancellationToken);
        return entries.Select(entry => new
        {
            entry.Id,
            entry.ParentId,
            Role = (entry as MessageEntry)?.Message.Role
        }).ToArray();
    }

    private async Task<RuntimeSessionChangeResult> DispatchSessionBeforeSwitchAsync(string reason, JsonlSessionMetadata? target, CancellationToken cancellationToken)
    {
        if (ExtensionManager is null) return new RuntimeSessionChangeResult(false);
        var evt = new ExtensionEvent(
            ExtensionEventNames.SessionBeforeSwitch,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeSwitch(reason, target?.Path, Session.Metadata, target, cancellationToken)),
            new ExtensionSessionBeforeSwitchEvent(reason, target?.Path, Session.Metadata, target));
        await DispatchRuntimeExtensionEventAsync(evt, cancellationToken);
        return evt.SessionChangeResult?.Cancel == true
            ? RuntimeSessionChangeResult.CancelledByExtension(evt.SessionChangeResult.Reason)
            : new RuntimeSessionChangeResult(false);
    }

    private async Task<RuntimeSessionChangeResult> DispatchSessionBeforeForkAsync(JsonlSessionMetadata source, SessionForkOptions forkOptions, CancellationToken cancellationToken)
    {
        if (ExtensionManager is null) return new RuntimeSessionChangeResult(false);
        var entryId = forkOptions.EntryId ?? string.Empty;
        var position = forkOptions.Position ?? "before";
        var evt = new ExtensionEvent(
            ExtensionEventNames.SessionBeforeFork,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionBeforeFork(entryId, position, source, forkOptions, cancellationToken)),
            new ExtensionSessionBeforeForkEvent(entryId, position, source, forkOptions));
        await DispatchRuntimeExtensionEventAsync(evt, cancellationToken);
        return evt.SessionChangeResult?.Cancel == true
            ? RuntimeSessionChangeResult.CancelledByExtension(evt.SessionChangeResult.Reason)
            : new RuntimeSessionChangeResult(false);
    }

    private async Task DispatchSessionShutdownAsync(string reason, string? targetSessionFile, CancellationToken cancellationToken)
    {
        if (ExtensionManager is null) return;
        var evt = new ExtensionEvent(
            ExtensionEventNames.SessionShutdown,
            new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionShutdown(reason, targetSessionFile, Session.Metadata)),
            new ExtensionSessionShutdownEvent(reason, targetSessionFile, Session.Metadata));
        try
        {
            await DispatchRuntimeExtensionEventAsync(evt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Ignoring extension session shutdown failure");
        }
    }

    private async Task DispatchRuntimeExtensionEventAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        if (ExtensionManager is null) return;
        foreach (var handler in ExtensionManager.Registry.HandlersFor(evt.Name))
        {
            await handler.Value.Handler(evt, cancellationToken);
        }
    }

    /// <summary>
    /// Emits a runtime-originated extension event (e.g. package/skill changes) to
    /// registered extension handlers without round-tripping through the harness loop.
    /// </summary>
    public Task EmitRuntimeExtensionEventAsync(string name, object? payload, CancellationToken cancellationToken = default)
    {
        var harnessEvent = new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.RuntimeEvent(name, payload));
        return DispatchRuntimeExtensionEventAsync(new ExtensionEvent(name, harnessEvent, payload), cancellationToken);
    }

    private async Task ApplySessionReplacementAsync(RuntimeSessionReplacement replacement, CancellationToken cancellationToken)
    {
        UnbindHarnessEventForwarding();
        _extensionDispatchSubscription?.Dispose();
        Session = replacement.Session;
        Harness = replacement.Harness;
        _extensionDispatchSubscription = replacement.ExtensionDispatchSubscription;
        BindHarnessEventForwarding();
        _sessionController.ExtensionBinder.RefreshResourceBinding();
        if (TsHost is not null) await TsHost.SetRuntimeSessionIdAsync(Session.Metadata.Id, cancellationToken);
        if (_rebind is not null) await _rebind(this, cancellationToken);
        await Harness.DispatchSessionStartAsync("replace", cancellationToken);
    }

    public async Task<ExtensionResourcesDiscoverResult> DispatchResourcesDiscoverAsync(CancellationToken cancellationToken = default, string reason = "startup")
    {
        var payload = new ExtensionResourcesDiscoverPayload(createOptions.Cwd, reason);
        var evt = new ExtensionEvent(ExtensionEventNames.ResourcesDiscover, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), payload);

        if (ExtensionManager is not null)
        {
            foreach (var handler in ExtensionManager.Registry.HandlersFor(ExtensionEventNames.ResourcesDiscover))
            {
                try
                {
                    await handler.Value.Handler(evt, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Report extension error and continue to later handlers
                }
            }
        }

        if (TsHost is not null)
        {
            try
            {
                await TsHost.ForwardExtensionEventAsync(evt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Report TS bridge error and continue
            }
        }

        return evt.ResourcesDiscoverResult ?? new ExtensionResourcesDiscoverResult([], [], []);
    }

    public async Task<RuntimeUserBashHookResult?> DispatchUserBashAsync(
        string command,
        bool excludeFromContext,
        CancellationToken cancellationToken)
    {
        var payload = new ExtensionUserBashPayload(command, excludeFromContext, createOptions.Cwd);
        var evt = new ExtensionEvent(ExtensionEventNames.UserBash, new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ResourcesUpdate(new object(), new object())), payload);

        if (ExtensionManager is not null)
        {
            foreach (var handler in ExtensionManager.Registry.HandlersFor(ExtensionEventNames.UserBash))
            {
                try
                {
                    await handler.Value.Handler(evt, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (HasUserBashResult(evt.UserBashResult))
                    return ToRuntimeResult(evt.UserBashResult!);
            }
        }

        if (TsHost is not null)
        {
            try
            {
                await TsHost.ForwardExtensionEventAsync(evt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Report TS bridge error and continue
            }

            if (HasUserBashResult(evt.UserBashResult))
                return ToRuntimeResult(evt.UserBashResult!);
        }

        return null;
    }

    private static bool HasUserBashResult(ExtensionUserBashResult? result)
        => result?.Result is not null || result?.Operations is not null;

    private static RuntimeUserBashHookResult ToRuntimeResult(ExtensionUserBashResult result)
        => new(result.Operations, result.Result);

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Session {SessionId} runtime disposing", Session.Metadata.Id);
        await _backgroundActivationCancellation.CancelAsync();
        Harness.Abort();
        await PersistCurrentModelSelectionAsync(CancellationToken.None);
        using (var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try
            {
                await DispatchSessionShutdownAsync("dispose", null, shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for extension session shutdown handlers during runtime disposal");
            }
        }
        _eventBridge.Dispose();
        _extensionDispatchSubscription?.Dispose();
        Telemetry?.Flush();
        await _sessionController.ExtensionBinder.DisposeAsync();
        await Harness.WaitForIdleAsync();
        if (TsHost is not null) await TsHost.DisposeAsync();
        await ExtensionLoadCoordinator.DisposeAsync();
        _backgroundActivationCancellation.Dispose();
    }
}
