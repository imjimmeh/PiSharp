using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using PiSharp.Runtime;

[assembly: ExtensionMetadata(
    "plan-mode",
    Name = "PiSharp Plan Mode",
    Version = "0.1.0",
    Description = "Read-only planning phase: restricted tools, optional planning model, plan files persisted under .pi/plans, and /plan approve|abort|end.",
    SourceId = "pi:extension:plan-mode")]

namespace PiSharp.PlanMode;

/// <summary>
/// <c>plan-mode</c> extension entry. Reads the <c>extensions.pisharp-plan-mode.*</c> settings and
/// the <c>--plan</c>/<c>--plan-model</c> flags, registers the <c>/plan</c> command, the prompt
/// contributor, and the capture/input-gate subscriptions, and owns the
/// <see cref="PlanModeService"/> state machine.
/// </summary>
public sealed class PlanModeExtension : IExtension, IPlanModeDaemonSurface, IAsyncDisposable
{
    internal const string SettingsNamespace = "pisharp-plan-mode";
    internal const string PlanFlag = "plan";
    internal const string PlanModelFlag = "plan-model";
    private static readonly IReadOnlyList<string> DefaultRestrictedToolNames = ["read", "grep", "find", "ls"];

    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private IExtensionApi? _api;
    private PlanModeService? _service;
    private bool _disposed;

    private bool _planModeEnabled;
    private string? _planningModel;
    private IReadOnlyList<string> _restrictedTools = DefaultRestrictedToolNames;
    private string _planFilesDir = ".pi/plans";

    internal PlanModeService? Service
    {
        get { lock (_gate) return _service; }
    }

    /// <summary>Current machine snapshot for daemon <c>get_plan_mode</c> (never throws).</summary>
    public ExtensionPlanModeState Current => ToDaemonState(Service?.State ?? new PlanModeState(PlanModePhase.Inactive, [], null, null));

    /// <summary>
    /// Drives the plan-mode machine from the daemon (<c>set_plan_mode</c>): <c>planning</c> enters,
    /// <c>executing</c> approves, <c>aborted</c> aborts, <c>inactive</c> ends. Entering a phase the
    /// machine is already in is a no-op; illegal transitions throw so the daemon can surface them.
    /// </summary>
    public async Task<ExtensionPlanModeState> ApplyPhaseAsync(string phase, CancellationToken cancellationToken = default)
    {
        var service = Service ?? throw new InvalidOperationException("The plan-mode service is not initialized.");
        PlanModeState state = phase switch
        {
            "planning" => service.Phase is PlanModePhase.Planning or PlanModePhase.Executing
                ? service.State
                : await EnterCoreAsync(service, null, cancellationToken),
            "executing" => await service.ApproveAsync(cancellationToken),
            "aborted" => await service.AbortAsync(cancellationToken),
            "inactive" => service.Phase is PlanModePhase.Inactive or PlanModePhase.Aborted
                ? service.State
                : await service.EndAsync(cancellationToken),
            _ => throw new ArgumentException($"Unknown plan-mode phase '{phase}'. Expected 'planning', 'executing', 'aborted', or 'inactive'.", nameof(phase))
        };
        return ToDaemonState(state);
    }

    private static ExtensionPlanModeState ToDaemonState(PlanModeState state)
        => new(PlanModeService.PhaseToString(state.Phase), state.RestrictedToolNames, state.PlanningModel, state.PlanFile);

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        ReadSettings(api);

        _subscriptions.Add(api.RegisterFlag(new ExtensionFlagRegistration(PlanFlag, "Start the session in plan mode (read-only planning phase).")));
        _subscriptions.Add(api.RegisterFlag(new ExtensionFlagRegistration(PlanModelFlag, "Model id for the planning phase (provider/model[:thinking]).", ExtensionFlagType.String)));

        var fileStore = new PlanFileStore(ResolvePlanFilesDir(api, _planFilesDir));
        var service = new PlanModeService(
            api,
            fileStore,
            await ResolveSessionIdAsync(api, cancellationToken),
            resolvePlanningModel: ResolvePlanningModelAsync);
        lock (_gate) _service = service;

        _subscriptions.Add(api.Prompt.RegisterContributor(new PlanModePromptContributor(service)));
        _subscriptions.Add(api.On(ExtensionEventNames.AgentEnd, new PlanCaptureHandler(service).OnAgentEndAsync));
        _subscriptions.Add(api.On(ExtensionEventNames.Input, new PlanModeInputGate(service).OnInputAsync));
        _subscriptions.Add(api.On(ExtensionEventNames.SessionStart, OnSessionStartAsync));
        _subscriptions.Add(api.On(ExtensionEventNames.ResourcesDiscover, OnResourcesDiscoverAsync));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration(
            "plan",
            "Plan mode: /plan (enter/status), /plan approve, /plan abort, /plan end.",
            OnPlanCommandAsync)));
    }

    private Task OnSessionStartAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        var api = _api;
        var service = Service;
        if (api is null || service is null) return Task.CompletedTask;

        // Flags are applied after InitializeAsync; the session_start event is the first
        // chance to observe them before any prompt can run.
        var flagEnabled = api.GetFlag(PlanFlag) is true;
        var flagModel = api.GetFlag(PlanModelFlag) as string;
        var enabled = flagEnabled || _planModeEnabled;
        if (!enabled) return Task.CompletedTask;

        var planningModel = !string.IsNullOrWhiteSpace(flagModel) ? flagModel : _planningModel;
        return TryEnterAsync(service, planningModel, cancellationToken);
    }

    private Task OnResourcesDiscoverAsync(ExtensionEvent evt, CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null) return Task.CompletedTask;
        ReadSettings(api);
        return Task.CompletedTask;
    }

    private async Task TryEnterAsync(PlanModeService service, string? planningModelOverride, CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null) return;
        try
        {
            await EnterCoreAsync(service, planningModelOverride, cancellationToken);
        }
        catch (Exception ex)
        {
            await api.SendMessageAsync(AgentMessages.User($"[plan mode] Failed to enter plan mode: {ex.Message}"), cancellationToken);
        }
    }

    private async Task<PlanModeState> EnterCoreAsync(PlanModeService service, string? planningModelOverride, CancellationToken cancellationToken)
    {
        var api = _api ?? throw new InvalidOperationException("The plan-mode extension is not initialized.");
        return await service.EnterAsync(new PlanModeOptions(_restrictedTools, planningModelOverride ?? _planningModel, _planFilesDir, await ResolveSessionIdAsync(api, cancellationToken)), cancellationToken);
    }

    private async Task OnPlanCommandAsync(string args, CancellationToken cancellationToken)
    {
        var api = _api;
        var service = Service;
        if (api is null || service is null) return;

        var command = args.Trim().ToLowerInvariant();
        switch (command)
        {
            case "" or "enter" or "start":
                if (service.Phase is PlanModePhase.Planning or PlanModePhase.Executing)
                {
                    await ReplyAsync(api, FormatState(service.State), cancellationToken);
                }
                else
                {
                    await TryEnterAsync(service, null, cancellationToken);
                }
                break;

            case "approve":
                await RunTransitionAsync(api, () => service.ApproveAsync(cancellationToken), "Plan approved. Executing with full tools and the approved plan.", cancellationToken);
                break;

            case "abort":
                await RunTransitionAsync(api, () => service.AbortAsync(cancellationToken), "Plan aborted. Full tools and model restored.", cancellationToken);
                break;

            case "end":
                await RunTransitionAsync(api, () => service.EndAsync(cancellationToken), "Plan mode ended.", cancellationToken);
                break;

            default:
                await ReplyAsync(api, "Usage: /plan [approve|abort|end]", cancellationToken);
                break;
        }
    }

    private async Task RunTransitionAsync(IExtensionApi api, Func<Task<PlanModeState>> transition, string successMessage, CancellationToken cancellationToken)
    {
        try
        {
            await transition();
            await ReplyAsync(api, successMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            await ReplyAsync(api, $"[plan mode] {ex.Message}", cancellationToken);
        }
    }

    private static async Task ReplyAsync(IExtensionApi api, string text, CancellationToken cancellationToken)
        => await api.SendMessageAsync(AgentMessages.User(text), cancellationToken);

    private static string FormatState(PlanModeState state)
        => $"[plan mode] phase={PlanModeService.PhaseToString(state.Phase)}, restrictedTools=[{string.Join(", ", state.RestrictedToolNames)}], planningModel={state.PlanningModel ?? "current"}, planFile={state.PlanFile ?? "none"}";

    private void ReadSettings(IExtensionApi api)
    {
        _planModeEnabled = api.Settings.Get<bool?>("planModeEnabled") ?? false;
        _planningModel = api.Settings.Get<string>("planningModel");
        _restrictedTools = api.Settings.Get<List<string>>("restrictedTools") ?? DefaultRestrictedToolNames;
        _planFilesDir = api.Settings.Get<string>("planFilesDir") ?? ".pi/plans";
    }

    private static string ResolvePlanFilesDir(IExtensionApi api, string configured)
        => Path.IsPathRooted(configured) ? configured : Path.Combine(api.Cwd, configured);

    private static async Task<string> ResolveSessionIdAsync(IExtensionApi api, CancellationToken cancellationToken)
    {
        try
        {
            var name = await api.Session.GetNameAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch (Exception)
        {
            // Session name unavailable — fall back to "default".
        }
        return "default";
    }

    private static Task<ModelDescriptor?> ResolvePlanningModelAsync(string? modelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return Task.FromResult<ModelDescriptor?>(null);
        var selection = RuntimeModelSelector.Resolve(new RuntimeModelSelectionRequest(null, modelId, null));
        return Task.FromResult<ModelDescriptor?>(selection.Model);
    }

    public ValueTask DisposeAsync()
    {
        List<IDisposable> subscriptions;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
        return ValueTask.CompletedTask;
    }
}
