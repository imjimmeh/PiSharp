using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;

namespace PiSharp.PlanMode;

/// <summary>
/// Entry configuration for <see cref="PlanModeService.EnterAsync"/>.
/// </summary>
public sealed record PlanModeOptions(
    IReadOnlyList<string> RestrictedTools,
    string? PlanningModel,
    string PlanFilesDir,
    string SessionId);

/// <summary>
/// Plan-mode state machine. Drives the C1 tool restriction + model switch and emits
/// exactly one <c>plan_mode_changed</c> client event per phase transition via C4.
/// </summary>
public sealed class PlanModeService
{
    public const string PlanModeChangedEvent = "plan_mode_changed";
    public const string WebSearchTool = "web_search";

    private readonly IExtensionApi _api;
    private readonly Func<string?, CancellationToken, Task<ModelDescriptor?>> _resolvePlanningModel;
    private readonly PlanFileStore _fileStore;
    private readonly string _sessionId;
    private PlanModeState _state = new(PlanModePhase.Inactive, [], null, null);
    private ModelDescriptor? _capturedModel;
    private string? _lastPlanBody;

    public PlanModeService(
        IExtensionApi api,
        PlanFileStore fileStore,
        string sessionId,
        Func<string?, CancellationToken, Task<ModelDescriptor?>>? resolvePlanningModel = null)
    {
        _api = api;
        _fileStore = fileStore;
        _sessionId = sessionId;
        _resolvePlanningModel = resolvePlanningModel ?? ((_, _) => Task.FromResult<ModelDescriptor?>(null));
    }

    public PlanModePhase Phase => _state.Phase;
    public PlanModeState State => _state;
    public string? LastPlanBody => _lastPlanBody;
    public string PlanFile => _fileStore.BuildPlanPath(_sessionId);

    /// <summary>
    /// Enters the <see cref="PlanModePhase.Planning"/> phase from <see cref="PlanModePhase.Inactive"/>
    /// or <see cref="PlanModePhase.Aborted"/>: captures the current model, restricts active tools to
    /// the effective read-only set, applies the planning model (when configured), and emits
    /// <c>plan_mode_changed</c> exactly once.
    /// </summary>
    public async Task<PlanModeState> EnterAsync(PlanModeOptions options, CancellationToken cancellationToken = default)
    {
        if (_state.Phase is PlanModePhase.Planning or PlanModePhase.Executing)
            throw new InvalidOperationException($"Cannot enter plan mode from phase '{_state.Phase}'.");

        _capturedModel = await _api.Model.GetModelAsync(cancellationToken);

        var registered = await _api.Tools.GetAllToolsAsync(cancellationToken);
        var effective = ComputeEffectiveRestrictedTools(options.RestrictedTools, registered);
        await _api.Tools.SetActiveToolsAsync(effective, cancellationToken);

        string? planningModelId = null;
        if (!string.IsNullOrWhiteSpace(options.PlanningModel))
        {
            var resolved = await _resolvePlanningModel(options.PlanningModel, cancellationToken);
            if (resolved is not null)
            {
                await _api.Model.SetModelAsync(resolved, cancellationToken);
                planningModelId = resolved.Id;
            }
        }

        _lastPlanBody = null;
        _state = new PlanModeState(PlanModePhase.Planning, effective, planningModelId, PlanFile);
        await EmitTransitionAsync(cancellationToken);
        return _state;
    }

    /// <summary>
    /// <see cref="PlanModePhase.Planning"/> → <see cref="PlanModePhase.Executing"/>: restores full
    /// tools and the captured model, marks the plan file <c>approved</c>, and keeps the approved
    /// body for prompt injection. Fails when no plan body has been captured yet.
    /// </summary>
    public async Task<PlanModeState> ApproveAsync(CancellationToken cancellationToken = default)
    {
        EnsurePhase(PlanModePhase.Planning, nameof(ApproveAsync));
        if (string.IsNullOrWhiteSpace(_lastPlanBody))
            throw new InvalidOperationException("Cannot approve a plan: no plan body has been captured yet.");

        await RestoreToolsAndModelAsync(cancellationToken);
        await _fileStore.SetStatusAsync(_state.PlanFile!, PlanFileStatus.Approved, _sessionId, _state.PlanningModel, cancellationToken);
        _state = new PlanModeState(PlanModePhase.Executing, [], _state.PlanningModel, _state.PlanFile);
        await EmitTransitionAsync(cancellationToken);
        return _state;
    }

    /// <summary>
    /// <see cref="PlanModePhase.Planning"/> → <see cref="PlanModePhase.Aborted"/>: restores full
    /// tools and the captured model and marks the plan file <c>aborted</c>.
    /// </summary>
    public async Task<PlanModeState> AbortAsync(CancellationToken cancellationToken = default)
    {
        EnsurePhase(PlanModePhase.Planning, nameof(AbortAsync));
        await RestoreToolsAndModelAsync(cancellationToken);
        if (_state.PlanFile is not null)
            await _fileStore.SetStatusAsync(_state.PlanFile, PlanFileStatus.Aborted, _sessionId, _state.PlanningModel, cancellationToken);
        _state = new PlanModeState(PlanModePhase.Aborted, [], _state.PlanningModel, _state.PlanFile);
        await EmitTransitionAsync(cancellationToken);
        return _state;
    }

    /// <summary>
    /// <see cref="PlanModePhase.Executing"/> → <see cref="PlanModePhase.Inactive"/>: stops injecting
    /// the approved plan. Tools and model were already restored at approval.
    /// </summary>
    public async Task<PlanModeState> EndAsync(CancellationToken cancellationToken = default)
    {
        EnsurePhase(PlanModePhase.Executing, nameof(EndAsync));
        _lastPlanBody = null;
        _state = new PlanModeState(PlanModePhase.Inactive, [], null, _state.PlanFile);
        await EmitTransitionAsync(cancellationToken);
        return _state;
    }

    /// <summary>
    /// Persists the last assistant message of a planning turn as a draft plan file and records it
    /// as the approval candidate body. No-op outside <see cref="PlanModePhase.Planning"/>.
    /// </summary>
    public async Task CapturePlanAsync(string body, CancellationToken cancellationToken = default)
    {
        if (_state.Phase != PlanModePhase.Planning) return;
        _lastPlanBody = body;
        await _fileStore.WriteDraftAsync(PlanFile, body, _sessionId, _state.PlanningModel, cancellationToken);
        _state = _state with { PlanFile = PlanFile };
    }

    /// <summary>
    /// Effective restricted set: configured names intersected with registered tools (stale names
    /// dropped), plus <c>web_search</c> whenever a tool with that name is registered.
    /// </summary>
    public static IReadOnlyList<string> ComputeEffectiveRestrictedTools(IReadOnlyList<string> restricted, IReadOnlyList<string> registered)
    {
        var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);
        var effective = restricted.Where(registeredSet.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (registeredSet.Contains(WebSearchTool) && !effective.Contains(WebSearchTool, StringComparer.Ordinal))
            effective.Add(WebSearchTool);
        return effective;
    }

    internal static string PhaseToString(PlanModePhase phase) => phase switch
    {
        PlanModePhase.Inactive => "inactive",
        PlanModePhase.Planning => "planning",
        PlanModePhase.Executing => "executing",
        PlanModePhase.Aborted => "aborted",
        _ => phase.ToString().ToLowerInvariant()
    };

    private async Task RestoreToolsAndModelAsync(CancellationToken cancellationToken)
    {
        await _api.Tools.SetActiveToolsAsync(null, cancellationToken);
        if (_capturedModel is not null)
            await _api.Model.SetModelAsync(_capturedModel, cancellationToken);
    }

    private void EnsurePhase(PlanModePhase expected, string operation)
    {
        if (_state.Phase != expected)
            throw new InvalidOperationException($"Cannot {operation} while phase is '{_state.Phase}'.");
    }

    private Task EmitTransitionAsync(CancellationToken cancellationToken)
        => _api.EmitClientEventAsync(
            PlanModeChangedEvent,
            new
            {
                phase = PhaseToString(_state.Phase),
                restrictedToolNames = _state.RestrictedToolNames,
                planningModel = _state.PlanningModel,
                planFile = _state.PlanFile
            },
            cancellationToken);
}
