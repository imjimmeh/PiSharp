using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Harness;
using PiSharp.Ai.Models;
using PiSharp.Compatibility.Settings;
using System.Runtime.CompilerServices;

namespace PiSharp.Runtime;

internal sealed class RuntimeModelController(
    PiSettingsStore? settingsStore,
    PiSettingsSnapshot? settingsSnapshot,
    ILoggerFactory? loggerFactory = null)
{
    private PiSettingsSnapshot? _settingsSnapshot = settingsSnapshot;
    private RuntimeModelSelection? _pendingPersistence;
    private readonly ILogger _logger = loggerFactory?.CreateLogger<RuntimeModelController>() ?? NullLogger<RuntimeModelController>.Instance;

    public async Task<RuntimeModelSelection> SetModelAsync(
        AgentHarness<JsonlSessionMetadata> harness,
        RuntimeModelSelection selection,
        string source = "runtime",
        CancellationToken cancellationToken = default)
    {
        await harness.SetModelAsync(selection.Model, source, cancellationToken);
        _pendingPersistence = selection;
        return selection;
    }

    public Task<RuntimeModelSelection> SetModelAsync(
        AgentHarness<JsonlSessionMetadata> harness,
        RuntimeModelSelection currentSelection,
        ModelDescriptor model,
        string source = "runtime",
        CancellationToken cancellationToken = default)
        => SetModelAsync(harness, currentSelection with { Model = model }, source, cancellationToken);

    public async Task<RuntimeModelSelection> SetThinkingLevelAsync(
        AgentHarness<JsonlSessionMetadata> harness,
        RuntimeModelSelection currentSelection,
        ThinkingLevel level,
        CancellationToken cancellationToken = default)
    {
        var harnessId = RuntimeHelpers.GetHashCode(harness);
        var clamped = ModelRegistry.ClampThinkingLevel(harness.Model, level);
        _logger.LogDebug(
            "Runtime model controller applying thinking level harnessId={HarnessId} model={Provider}/{ModelId} currentSelectionThinking={CurrentSelectionThinking} requestedLevel={RequestedLevel} clampedLevel={ClampedLevel}",
            harnessId,
            harness.Model.Provider,
            harness.Model.Id,
            currentSelection.ThinkingLevel,
            level,
            clamped);
        var next = currentSelection with { ThinkingLevel = clamped };
        _pendingPersistence = next;
        await harness.SetThinkingLevelAsync(clamped, cancellationToken);
        _logger.LogDebug(
            "Runtime model controller updated thinking level harnessId={HarnessId} harnessThinking={HarnessThinking} pendingPersistenceThinking={PendingPersistenceThinking}",
            harnessId,
            harness.ThinkingLevel,
            next.ThinkingLevel);
        return next;
    }

    public async Task PersistPendingSelectionAsync(CancellationToken cancellationToken = default)
    {
        var pending = _pendingPersistence;
        if (pending is null) return;
        _pendingPersistence = null;
        _logger.LogDebug(
            "Runtime model controller persisting pending selection model={Provider}/{ModelId} thinking={ThinkingLevel}",
            pending.Model.Provider,
            pending.Model.Id,
            pending.ThinkingLevel);
        await PersistModelSelectionAsync(pending, cancellationToken);
    }

    private async Task PersistModelSelectionAsync(RuntimeModelSelection selection, CancellationToken cancellationToken)
    {
        if (settingsStore is null || _settingsSnapshot is null) return;

        var providerLayer = _settingsSnapshot.SourceLayerFor("defaultProvider") ?? PiSettingsLayer.GlobalLegacy;
        var modelLayer = _settingsSnapshot.SourceLayerFor("defaultModel") ?? providerLayer;
        var thinkingLayer = _settingsSnapshot.SourceLayerFor("defaultThinking") ?? modelLayer;
        var serializedThinking = selection.ThinkingLevel.ToString().ToLowerInvariant();
        var layers = new[] { providerLayer, modelLayer, thinkingLayer }.Distinct().ToArray();

        foreach (var layer in layers)
        {
            await settingsStore.SaveLayerAsync(_settingsSnapshot, layer, document =>
            {
                if (layer == providerLayer) document.SetString("defaultProvider", selection.Model.Provider);
                if (layer == modelLayer) document.SetString("defaultModel", selection.Model.Id);
                if (layer == thinkingLayer) document.SetString("defaultThinking", serializedThinking);
            }, cancellationToken);
        }

        _settingsSnapshot = await settingsStore.LoadAsync(_settingsSnapshot.Paths.Cwd, _settingsSnapshot.Paths.HomeDirectory, cancellationToken);
    }
}
