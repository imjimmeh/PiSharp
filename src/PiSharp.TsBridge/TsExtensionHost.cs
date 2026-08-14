using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Prompting;
using PiSharp.Agent.Messages;
using PiSharp.Agent.Resources.Prompting;
using PiSharp.Agent.Serialization;
using PiSharp.Ai;
using PiSharp.Extensions;
using PiSharp.TsBridge.Protocol;
using PiSharp.TsBridge.Shims;

namespace PiSharp.TsBridge;

public sealed class TsExtensionHost(TsBridgeOptions options, ExtensionRegistry registry, ExtensionRuntimeBinding? runtimeBinding = null, ILoggerFactory? loggerFactory = null) : IAsyncDisposable
{
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;
    private readonly ILogger _logger = loggerFactory?.CreateLogger<TsExtensionHost>() ?? NullLogger<TsExtensionHost>.Instance;
    private readonly ConcurrentDictionary<string, Task<TsExtensionLoadResult>> _activationTasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _loadedExtensions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _descriptorBackedExtensions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _descriptorSourcesReplaced = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _messageRendererHandles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _messageDecoratorHandles = new(StringComparer.Ordinal);
    private readonly TsDescriptorCache _descriptorCache = new(options.CacheEnabled, options.CacheDirectory);
    private readonly ITsBridgeClient _client = new NodeTsBridgeClient(options, loggerFactory: loggerFactory);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Func<TsUiRequest, Task<object?>>? _uiBridge;
    private ExtensionRuntimeBinding? _runtimeBinding = runtimeBinding;
    private readonly List<Exception> _emitDiagnostics = [];
    private readonly SdkShimRuntimeDispatcher _sdkDispatcher = new();
    private ExtensionRuntimeBinding? _childEventForwardingBinding;
    private Func<string, object, CancellationToken, Task>? _childEventForwarder;
    private const int ChildSessionEventBatchCapacity = 4096;
    private const int ChildSessionEventBatchSize = 64;
    private static readonly TimeSpan ChildSessionEventBatchInterval = TimeSpan.FromMilliseconds(16);
    private CancellationTokenSource? _childEventBatchCancellation;
    private Channel<ChildSessionEventForward>? _childEventBatchQueue;
    private Task? _childEventBatchWorker;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _uiReadySessionStartForwarded;

    public IReadOnlyList<string> RecentStandardError => _client.RecentStandardError;
    public IReadOnlyList<Exception> EmitDiagnostics => _emitDiagnostics;

    public void SetUiBridge(Func<TsUiRequest, Task<object?>>? uiBridge) => _uiBridge = uiBridge;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _startGate.WaitAsync(linkedCts.Token);
        try
        {
            if (_client.IsStarted) return;

            var initializePayload = new
            {
                extensionPaths = options.ExtensionPaths ?? [],
                cacheDirectory = options.CacheDirectory,
                cacheEnabled = options.CacheEnabled,
                hasUi = _runtimeBinding?.HasUi ?? false,
                sessionId = await RuntimeSessionIdAsync(linkedCts.Token),
                session = await RuntimeSessionSnapshotAsync(linkedCts.Token),
                commands = await RuntimeCommandsAsync(linkedCts.Token),
                bridgeManifest = TsBridgeManifestFactory.CreateDefault()
            };

            WireEmitDelegateIfNeeded();
            WireChildSessionEventForwarding();
            await _client.StartAsync(HandleRequestAsync, initializePayload, linkedCts.Token);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<TsExtensionLoadResult> LoadAsync(string extensionPath, ExtensionRuntimeBinding binding, CancellationToken cancellationToken = default)
    {
        var result = await LoadManyAsync([extensionPath], binding, cancellationToken);
        return result.Results?.FirstOrDefault()
            ?? new TsExtensionLoadResult(false, extensionPath, "TypeScript bridge returned an empty load result.");
    }

    public async Task<TsExtensionsLoadResult> LoadManyAsync(IReadOnlyList<string> extensionPaths, ExtensionRuntimeBinding binding, CancellationToken cancellationToken = default)
    {
        _runtimeBinding = binding;
        WireEmitDelegateIfNeeded();
        WireChildSessionEventForwarding();
        await StartAsync(cancellationToken);
        if (!_client.IsStarted)
        {
            return new TsExtensionsLoadResult(false, extensionPaths.Select(path => new TsExtensionLoadResult(false, path, "TypeScript bridge is not running.")).ToArray());
        }
        _logger.LogDebug($"bridge: waiting for load gate ({extensionPaths.Count} paths)");
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            _logger.LogDebug($"bridge: load gate acquired, sending load_extensions RPC ({extensionPaths.Count} paths)");
            var response = await _client.RequestAsync("load_extensions", new TsExtensionsLoadRequest(extensionPaths, HasUi: binding.HasUi, SessionId: await RuntimeSessionIdAsync(cancellationToken), Commands: await RuntimeCommandsAsync(cancellationToken), Session: await RuntimeSessionSnapshotAsync(cancellationToken)), cancellationToken);
            _logger.LogDebug($"bridge: load_extensions RPC response received ({extensionPaths.Count} paths)");
            var result = AgentJsonSerializer.Deserialize<TsExtensionsLoadResult>(response.GetRawText())
                ?? new TsExtensionsLoadResult(false, extensionPaths.Select(path => new TsExtensionLoadResult(false, path, "TypeScript bridge returned an empty load result.")).ToArray());
            foreach (var loadResult in result.Results ?? [])
            {
                if (loadResult.Ok && !string.IsNullOrWhiteSpace(loadResult.ExtensionPath)) _loadedExtensions[loadResult.ExtensionPath] = true;
                if (loadResult is { Ok: true, Descriptor: not null }) await _descriptorCache.PersistAsync(loadResult.Descriptor, cancellationToken);
            }
            return result;
        }
        finally
        {
            _logger.LogDebug("bridge: releasing load gate");
            _loadGate.Release();
        }
    }

    public async Task<bool> ReplayCachedDescriptorAsync(string extensionPath, ExtensionRuntimeBinding binding, CancellationToken cancellationToken = default)
    {
        _runtimeBinding = binding;
        var descriptor = await _descriptorCache.ReadAsync(extensionPath, cancellationToken);
        if (descriptor is null) return false;
        if (RequiresEagerActivation(descriptor)) return false;
        RegisterDescriptor(descriptor);
        _descriptorBackedExtensions[extensionPath] = true;
        return true;
    }

    public async Task<TsExtensionLoadResult> EnsureExtensionActivatedAsync(string extensionPath, CancellationToken cancellationToken = default)
    {
        if (_loadedExtensions.ContainsKey(extensionPath)) return new TsExtensionLoadResult(true, extensionPath);
        var task = _activationTasks.GetOrAdd(extensionPath, path => ActivateExtensionAsync(path, cancellationToken));
        return await task;
    }

    public Task<TsExtensionLoadResult> ActivateExtensionInBackgroundAsync(string extensionPath, ExtensionRuntimeBinding binding, CancellationToken cancellationToken = default)
    {
        _runtimeBinding = binding;
        return _activationTasks.GetOrAdd(extensionPath, path => ActivateExtensionsInBackgroundAsync([path], binding, cancellationToken).ContinueWith(
            task => task.Result.FirstOrDefault(result => string.Equals(result.ExtensionPath, path, StringComparison.Ordinal))
                ?? new TsExtensionLoadResult(false, path, "Background batch did not return a result."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));
    }

    public Task<IReadOnlyList<TsExtensionLoadResult>> ActivateExtensionsInBackgroundAsync(IReadOnlyList<string> extensionPaths, ExtensionRuntimeBinding binding, CancellationToken cancellationToken = default)
    {
        _runtimeBinding = binding;
        var paths = extensionPaths.Where(path => !_loadedExtensions.ContainsKey(path)).Distinct(StringComparer.Ordinal).ToArray();
        var scheduledPaths = paths.Where(path => !_activationTasks.ContainsKey(path)).ToArray();
        _logger.LogDebug("bridge: background activation requested ({Requested} paths, {Scheduled} scheduled)", extensionPaths.Count, scheduledPaths.Length);
        var startTask = Task.Factory.StartNew(
            () => RunWithoutSynchronizationContext(() => StartBackgroundActivationBatchAsync(scheduledPaths, binding, cancellationToken)),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
        var completions = scheduledPaths.ToDictionary(
            path => path,
            _ => new TaskCompletionSource<TsExtensionLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            StringComparer.Ordinal);
        foreach (var extensionPath in scheduledPaths)
        {
            _logger.LogDebug("bridge: background activation poll task registered {ExtensionPath}", extensionPath);
            _activationTasks.TryAdd(extensionPath, completions[extensionPath].Task);
        }

        if (scheduledPaths.Length > 0)
        {
            _ = PollBackgroundActivationBatchAsync(startTask, scheduledPaths, completions, cancellationToken);
        }

        var tasks = paths.Select(path => _activationTasks.GetOrAdd(path, fallbackPath => ActivateExtensionAsync(fallbackPath, cancellationToken))).ToArray();

        return CompleteBackgroundActivationBatchAsync(extensionPaths, tasks);
    }

    private static T RunWithoutSynchronizationContext<T>(Func<T> work)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return work();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private async Task<TsExtensionLoadResult> ActivateExtensionAsync(string extensionPath, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsExtensionLoadResult(false, extensionPath, "Extension runtime is not bound.");
        return await LoadAsync(extensionPath, _runtimeBinding, cancellationToken);
    }

    private async Task StartBackgroundActivationBatchAsync(IReadOnlyList<string> extensionPaths, ExtensionRuntimeBinding binding, CancellationToken cancellationToken)
    {
        _runtimeBinding = binding;
        WireEmitDelegateIfNeeded();
        WireChildSessionEventForwarding();
        await StartAsync(cancellationToken);
        if (extensionPaths.Count == 0) return;
        if (!_client.IsStarted) throw new InvalidOperationException("TypeScript bridge is not running.");

        _logger.LogDebug("bridge: sending start_background_load_extensions RPC ({Count} paths)", extensionPaths.Count);
        await _client.RequestAsync("start_background_load_extensions", new
        {
            extensionPaths,
            concurrency = 4,
            hasUi = binding.HasUi,
            sessionId = await RuntimeSessionIdAsync(cancellationToken),
            commands = await RuntimeCommandsAsync(cancellationToken),
            session = await RuntimeSessionSnapshotAsync(cancellationToken)
        }, cancellationToken);
        _logger.LogDebug("bridge: start_background_load_extensions RPC acknowledged ({Count} paths)", extensionPaths.Count);
    }

    private async Task<TsExtensionLoadResult> PollBackgroundActivationAsync(Task startTask, string extensionPath, CancellationToken cancellationToken)
    {
        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TsExtensionLoadResult(false, extensionPath, "Background activation was canceled.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TsExtensionLoadResult(false, extensionPath, exception.Message);
        }

        if (!_client.IsStarted) return new TsExtensionLoadResult(false, extensionPath, "TypeScript bridge is not running.");
        var pollCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await _client.RequestAsync("background_load_status", new { extensionPath }, cancellationToken);
            var status = AgentJsonSerializer.Deserialize<TsExtensionBackgroundLoadStatus>(response.GetRawText())
                ?? new TsExtensionBackgroundLoadStatus(Error: "TypeScript bridge returned an empty background load status.");
            if (!status.Complete)
            {
                pollCount++;
                if (pollCount is 1 or 10 or 50 or 100 or 300)
                {
                    _logger.LogDebug("bridge: background activation still pending {ExtensionPath} (poll {PollCount})", extensionPath, pollCount);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var result = status.Result ?? new TsExtensionLoadResult(false, extensionPath, status.Error ?? "TypeScript bridge returned an empty background load result.");
            _logger.LogDebug("bridge: background activation {Result} {ExtensionPath}{Error}", result.Ok ? "ok" : "failed", extensionPath, result.Error is null ? string.Empty : $" error={result.Error}");
            if (result.Ok && !string.IsNullOrWhiteSpace(result.ExtensionPath)) _loadedExtensions[result.ExtensionPath] = true;
            if (result is { Ok: true, Descriptor: not null }) await _descriptorCache.PersistAsync(result.Descriptor, cancellationToken);
            return result;
        }

        return new TsExtensionLoadResult(false, extensionPath, "Background activation was canceled.");
    }

    private async Task PollBackgroundActivationBatchAsync(
        Task startTask,
        IReadOnlyList<string> extensionPaths,
        IReadOnlyDictionary<string, TaskCompletionSource<TsExtensionLoadResult>> completions,
        CancellationToken cancellationToken)
    {
        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var extensionPath in extensionPaths)
                completions[extensionPath].TrySetResult(new TsExtensionLoadResult(false, extensionPath, "Background activation was canceled."));
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var extensionPath in extensionPaths)
                completions[extensionPath].TrySetResult(new TsExtensionLoadResult(false, extensionPath, exception.Message));
            return;
        }

        if (!_client.IsStarted)
        {
            foreach (var extensionPath in extensionPaths)
                completions[extensionPath].TrySetResult(new TsExtensionLoadResult(false, extensionPath, "TypeScript bridge is not running."));
            return;
        }

        var pending = new HashSet<string>(extensionPaths, StringComparer.Ordinal);
        var pollCount = 0;
        try
        {
            while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var response = await _client.RequestAsync("background_load_statuses", new { extensionPaths = pending.ToArray() }, cancellationToken);
                var batchStatus = AgentJsonSerializer.Deserialize<TsExtensionBackgroundLoadStatuses>(response.GetRawText())
                    ?? new TsExtensionBackgroundLoadStatuses();
                foreach (var status in batchStatus.Statuses ?? [])
                {
                    if (!status.Complete || string.IsNullOrWhiteSpace(status.ExtensionPath)) continue;
                    var result = status.Result ?? new TsExtensionLoadResult(false, status.ExtensionPath, status.Error ?? "TypeScript bridge returned an empty background load result.");
                    _logger.LogDebug("bridge: background activation {Result} {ExtensionPath}{Error}", result.Ok ? "ok" : "failed", status.ExtensionPath, result.Error is null ? string.Empty : $" error={result.Error}");
                    if (result.Ok && !string.IsNullOrWhiteSpace(result.ExtensionPath)) _loadedExtensions[result.ExtensionPath] = true;
                    if (result is { Ok: true, Descriptor: not null }) await _descriptorCache.PersistAsync(result.Descriptor, cancellationToken);
                    completions[status.ExtensionPath].TrySetResult(result);
                    pending.Remove(status.ExtensionPath);
                }

                if (pending.Count == 0) break;
                pollCount++;
                if (pollCount is 1 or 10 or 50 or 100 or 300)
                {
                    _logger.LogDebug("bridge: background activation still pending {PendingCount} extensions (poll {PollCount})", pending.Count, pollCount);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        foreach (var extensionPath in pending)
            completions[extensionPath].TrySetResult(new TsExtensionLoadResult(false, extensionPath, "Background activation was canceled."));
    }

    private static async Task<IReadOnlyList<TsExtensionLoadResult>> CompleteBackgroundActivationBatchAsync(IReadOnlyList<string> extensionPaths, IReadOnlyList<Task<TsExtensionLoadResult>> tasks)
    {
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return extensionPaths.Select(path => results.FirstOrDefault(result => string.Equals(result.ExtensionPath, path, StringComparison.Ordinal))
            ?? new TsExtensionLoadResult(false, path, "Background batch did not return a result.")).ToArray();
    }

    public async Task SetRuntimeHasUiAsync(bool hasUi, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) return;
        await _client.RequestAsync("set_runtime_has_ui", new { hasUi }, cancellationToken);
        if (hasUi && !_uiReadySessionStartForwarded)
        {
            _uiReadySessionStartForwarded = true;
            await ForwardEventAsync(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.SessionStart("ui_ready")), cancellationToken);
        }
    }

    public async Task SetRuntimeSessionIdAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) return;
        await _client.RequestAsync("set_runtime_session", new { sessionId }, cancellationToken);
    }

    public async Task<TsCustomUiSnapshot> SendCustomUiInputAsync(TsCustomUiInputRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) throw new InvalidOperationException("TypeScript bridge is not running.");
        try
        {
            var result = await _client.RequestAsync("custom_ui_input", request, cancellationToken);
            return AgentJsonSerializer.Deserialize<TsCustomUiSnapshot>(result.GetRawText())
                ?? throw new InvalidOperationException("TypeScript bridge returned no custom UI snapshot.");
        }
        catch (InvalidOperationException exception)
        {
            if (IsMissingCustomUiInputMethodError(exception))
            {
                throw new InvalidOperationException("TypeScript bridge does not support custom UI input yet. Method 'custom_ui_input' is not implemented.", exception);
            }

            if (IsUnknownCustomUiSessionError(exception))
            {
                throw new InvalidOperationException(TryGetJsonRpcErrorMessage(exception.Message) ?? "Unknown custom UI session.", exception);
            }

            throw;
        }
    }

    private static bool IsMissingCustomUiInputMethodError(InvalidOperationException exception)
    {
        if (TryGetJsonRpcErrorCode(exception.Message) is -32601)
        {
            return true;
        }

        return exception.Message.Contains("custom_ui_input", StringComparison.OrdinalIgnoreCase)
            && (exception.Message.Contains("unknown bridge method", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("method not found", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnknownCustomUiSessionError(InvalidOperationException exception)
    {
        if (TryGetJsonRpcErrorCode(exception.Message) is -32000)
        {
            return exception.Message.Contains("unknown custom ui session", StringComparison.OrdinalIgnoreCase);
        }

        return exception.Message.Contains("unknown custom ui session", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetJsonRpcErrorMessage(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("message", out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String)
            {
                return errorMessage.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static int? TryGetJsonRpcErrorCode(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            if (TryReadCode(document.RootElement, "code", out var code) || TryReadCode(document.RootElement, "Code", out code))
            {
                return code;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool TryReadCode(JsonElement element, string propertyName, out int code)
    {
        code = default;
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out code);
    }

    private async Task<string?> RuntimeSessionIdAsync(CancellationToken cancellationToken)
        => _runtimeBinding is null ? null : await _runtimeBinding.GetSessionIdAsync(cancellationToken);

    private async Task<IReadOnlyList<ExtensionCommandInfo>> RuntimeCommandsAsync(CancellationToken cancellationToken)
        => _runtimeBinding is null ? [] : await _runtimeBinding.GetCommandsAsync(cancellationToken);

    private async Task<object?> RuntimeSessionSnapshotAsync(CancellationToken cancellationToken)
        => _runtimeBinding is null ? null : await _runtimeBinding.GetSessionSnapshotAsync(cancellationToken);

    public async Task ResetExtensionsAsync(CancellationToken cancellationToken = default)
    {
        _loadedExtensions.Clear();
        _activationTasks.Clear();
        _uiReadySessionStartForwarded = false;
        _descriptorBackedExtensions.Clear();
        _descriptorSourcesReplaced.Clear();
        if (_client.IsStarted) await _client.RequestAsync("reset_extensions", new { }, cancellationToken);
        await SignalAbortAsync();
    }

    public async Task ForwardEventAsync(AgentHarnessEvent evt, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) return;
        var mapped = ExtensionEventMapper.Map(evt);
        await _client.RequestAsync("event", new TsEventForward(mapped.Name, mapped.Payload), cancellationToken).ConfigureAwait(false);
    }

    public async Task ForwardExtensionEventAsync(ExtensionEvent evt, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) return;
        var result = await _client.RequestAsync("event", new TsEventForward(evt.Name, PayloadFor(evt)), cancellationToken).ConfigureAwait(false);
        var dispatch = AgentJsonSerializer.Deserialize<TsEventDispatchResult>(result.GetRawText());
        if (evt.Name == ExtensionEventNames.BeforeAgentStart)
        {
            evt.ModifyBeforeAgentStart(dispatch?.SystemPrompt, dispatch?.Messages);
        }
        else if (evt.Name == ExtensionEventNames.BeforePromptRender && dispatch?.Patch is not null)
        {
            var before = evt.OriginalEvent is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforePromptRender prompt }
                ? prompt
                : null;
            var applier = new PromptDocumentPatchApplier();
            var current = evt.ModifiedPromptDocument ?? before?.Document;
            if (current is not null && evt.ModifiedPromptDocumentPatch is not null)
            {
                current = applier.Apply(current, evt.ModifiedPromptDocumentPatch, new PromptContributionSource("extension:pre-ts", PromptContributionSourceKind.Extension));
            }

            if (current is not null)
            {
                evt.ModifyPromptDocument(applier.Apply(current, dispatch.Patch, new PromptContributionSource("extension:ts-bridge", PromptContributionSourceKind.Extension)));
            }
        }
        else if (evt.Name == ExtensionEventNames.Input && dispatch is not null)
        {
            if (string.Equals(dispatch.Action, "handled", StringComparison.OrdinalIgnoreCase)) evt.HandleInput();
            else if (string.Equals(dispatch.Action, "transform", StringComparison.OrdinalIgnoreCase) && dispatch.Text is not null) evt.TransformInput(dispatch.Text, dispatch.Images);
        }
        else if ((evt.Name == ExtensionEventNames.SessionBeforeSwitch || evt.Name == ExtensionEventNames.SessionBeforeFork) && dispatch?.Cancel == true)
        {
            evt.CancelSessionChange(dispatch.Reason);
        }
        else if (evt.Name == ExtensionEventNames.ResourcesDiscover && dispatch is not null)
        {
            if (dispatch.SkillPaths is { Length: > 0 } || dispatch.PromptPaths is { Length: > 0 } || dispatch.ThemePaths is { Length: > 0 })
            {
                evt.AddResourcesDiscoverPaths(dispatch.SkillPaths, dispatch.PromptPaths, dispatch.ThemePaths);
            }
        }
        else if (evt.Name == ExtensionEventNames.UserBash && dispatch is not null)
        {
            if (dispatch.BashResult is not null || dispatch.Operations is not null)
            {
                evt.SetUserBashResult(dispatch.Operations, dispatch.BashResult);
            }
        }
    }

    private static object? PayloadFor(ExtensionEvent evt)
    {
        if (evt.Name == ExtensionEventNames.BeforeAgentStart
            && evt.OriginalEvent is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeAgentStart before })
        {
            return new { before.Prompt, before.Images, SystemPrompt = evt.ModifiedSystemPrompt ?? before.SystemPrompt, before.Resources };
        }

        if (evt.Name == ExtensionEventNames.BeforePromptRender
            && evt.OriginalEvent is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforePromptRender prompt })
        {
            var applier = new PromptDocumentPatchApplier();
            var document = evt.ModifiedPromptDocument ?? prompt.Document;
            if (evt.ModifiedPromptDocumentPatch is not null)
            {
                document = applier.Apply(document, evt.ModifiedPromptDocumentPatch, new PromptContributionSource("extension:pre-ts", PromptContributionSourceKind.Extension));
            }
            return new PromptDocumentHookPayload(prompt.Prompt, applier.ToSectionDtos(document), document.Diagnostics, prompt.Resources);
        }

        return evt.Payload;
    }

    private void RegisterDescriptor(TsExtensionDescriptor descriptor)
    {
        var sourceId = SourceId(descriptor.ExtensionPath);
        foreach (var flag in descriptor.Flags ?? [])
        {
            RegisterFlagDescriptor(flag, sourceId);
        }
        foreach (var section in descriptor.PromptSections ?? []) RegisterPromptSectionDescriptor(section, sourceId);
        foreach (var transform in descriptor.PromptTransforms ?? []) registry.RegisterPromptTransform(sourceId, new TsStaticPromptTransform(transform));
        foreach (var provider in descriptor.Providers ?? []) RegisterProviderDescriptor(provider, sourceId);
        foreach (var skill in descriptor.Skills ?? []) RegisterSkillDescriptor(skill, sourceId);
        foreach (var tool in descriptor.Tools ?? []) registry.RegisterTool(sourceId, new TsBridgeTool(tool, this, EnsureExtensionActivatedAsync));
        foreach (var command in descriptor.Commands ?? []) registry.RegisterCommand(sourceId, new ExtensionCommandRegistration(
            command.Name,
            command.Description,
            async (args, token) =>
            {
                var result = await EnsureExtensionActivatedAsync(descriptor.ExtensionPath, token);
                if (!result.Ok) throw new InvalidOperationException(result.Error ?? $"Extension '{descriptor.ExtensionPath}' failed to activate.");
                await InvokeCommandAsync(command.ExtensionId, command.Name, args, token);
            }));
        foreach (var shortcut in descriptor.Shortcuts ?? []) registry.RegisterShortcut(sourceId, new ExtensionShortcutRegistration(
            shortcut.Keys,
            shortcut.Description,
            async (args, token) =>
            {
                var result = await EnsureExtensionActivatedAsync(descriptor.ExtensionPath, token);
                if (!result.Ok) throw new InvalidOperationException(result.Error ?? $"Extension '{descriptor.ExtensionPath}' failed to activate.");
                await InvokeCommandAsync(shortcut.ExtensionId, $"shortcut:{shortcut.Keys}", args, token);
            }));
    }

    private void RegisterFlagDescriptor(TsFlagRegistration registration, string sourceId)
    {
        var type = string.Equals(registration.Type, "string", StringComparison.OrdinalIgnoreCase) ? ExtensionFlagType.String : ExtensionFlagType.Boolean;
        var flag = new ExtensionFlagRegistration(registration.Name, registration.Description, type, registration.DefaultValue);
        _runtimeBinding?.RegisterFlag(flag);
        registry.RegisterFlag(sourceId, flag);
    }

    private void RegisterProviderDescriptor(TsProviderRegistration registration, string sourceId)
    {
        var provider = TsProviderAdapter.Register(registration.ToConfig(), sourceId, this, async token =>
        {
            var result = await EnsureExtensionActivatedAsync(registration.ExtensionId, token);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? $"Extension '{registration.ExtensionId}' failed to activate.");
        });
        if (provider is not null)
        {
            registry.RegisterProvider(sourceId, provider);
            PublicApi.RegisterProvider(provider, sourceId);
        }
    }

    private void RegisterSkillDescriptor(TsSkillRegistration registration, string sourceId)
    {
        registry.RegisterSkill(sourceId, ToExtensionSkillRegistration(registration), ParseOverride(registration.Override));
    }

    private void RegisterPromptSectionDescriptor(TsPromptSectionRegistration registration, string sourceId)
    {
        var section = new PromptSection(
            registration.Id,
            PromptSectionKind.Extension,
            new MarkdownPromptContent(registration.Content),
            new PromptPlacement(registration.Slot, registration.Priority),
            new PromptSectionOptions(Protected: registration.Protected));
        registry.RegisterPromptSection(sourceId, section, ParseOverride(registration.Override));
    }


    private void EnsureDescriptorSourceReplaced(string extensionId)
    {
        if (!_descriptorBackedExtensions.ContainsKey(extensionId)) return;
        var sourceId = SourceId(extensionId);
        if (_descriptorSourcesReplaced.TryAdd(sourceId, true))
        {
            PublicApi.UnregisterProviderSource(sourceId);
            registry.UnregisterBySource(sourceId);
        }
    }

    private Task<object?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
        => request.Method switch
        {
            TsBridgeMethods.RegisterTool => RegisterToolAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterSkill => RegisterSkillAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterProvider => RegisterProviderAsync(request.Params!, cancellationToken),
            TsBridgeMethods.UnregisterProvider => UnregisterProviderAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterCommand => RegisterCommandAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterShortcut => RegisterShortcutAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterFlag => RegisterFlagAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterPromptSection => RegisterPromptSectionAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterPromptTransform => RegisterPromptTransformAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterMessageRenderer => RegisterMessageRendererAsync(request.Params!, cancellationToken),
            TsBridgeMethods.UnregisterMessageRenderer => UnregisterMessageRendererAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RegisterMessageDecorator => RegisterMessageDecoratorAsync(request.Params!, cancellationToken),
            TsBridgeMethods.UnregisterMessageDecorator => UnregisterMessageDecoratorAsync(request.Params!, cancellationToken),
            TsBridgeMethods.RuntimeAction => RuntimeActionAsync(request.Params!, cancellationToken),
            TsBridgeMethods.UiRequest => ForwardUiRequestAsync(request.Params!, cancellationToken),
            _ => Task.FromResult<object?>(new JsonRpcError(-32601, $"Unknown bridge method '{request.Method}'."))
        };

    private Task<object?> RegisterToolAsync(object parameters, CancellationToken cancellationToken)
    {
        var definition = DeserializeObject<TsToolDefinition>(parameters)!;
        EnsureDescriptorSourceReplaced(definition.ExtensionId);
        registry.RegisterTool(SourceId(definition.ExtensionId), new TsBridgeTool(definition, this));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterSkillAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsSkillRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        registry.RegisterSkill(SourceId(registration.ExtensionId), ToExtensionSkillRegistration(registration), ParseOverride(registration.Override));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterProviderAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsProviderRegistration>(parameters);
        var config = registration?.ToConfig() ?? DeserializeObject<TsProviderConfig>(parameters)!;
        EnsureDescriptorSourceReplaced(registration?.ExtensionId ?? ExtensionId(parameters));
        var sourceId = ProviderSourceId(parameters, config.Name);
        var provider = TsProviderAdapter.Register(config, sourceId, this);
        if (provider is not null)
        {
            registry.RegisterProvider(sourceId, provider);
            PublicApi.RegisterProvider(provider, sourceId);
        }
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> UnregisterProviderAsync(object parameters, CancellationToken cancellationToken)
    {
        var name = ProviderName(parameters);
        var sourceId = ProviderSourceId(parameters, name);
        var removed = PublicApi.UnregisterProviderSource(sourceId) + registry.UnregisterBySource(sourceId);
        return Task.FromResult<object?>(new { ok = true, removed });
    }

    private Task<object?> RegisterCommandAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsCommandRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        _logger.LogDebug("register_command: extensionId={ExtensionId} name={CommandName}", registration.ExtensionId, registration.Name);
        registry.RegisterCommand(SourceId(registration.ExtensionId), new ExtensionCommandRegistration(
            registration.Name,
            registration.Description,
            async (args, token) => await InvokeCommandAsync(registration.ExtensionId, registration.Name, args, token)));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterShortcutAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsShortcutRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        registry.RegisterShortcut(SourceId(registration.ExtensionId), new ExtensionShortcutRegistration(
            registration.Keys,
            registration.Description,
            async (args, token) => await InvokeCommandAsync(registration.ExtensionId, $"shortcut:{registration.Keys}", args, token)));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterFlagAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsFlagRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        var type = string.Equals(registration.Type, "string", StringComparison.OrdinalIgnoreCase) ? ExtensionFlagType.String : ExtensionFlagType.Boolean;
        var flag = new ExtensionFlagRegistration(registration.Name, registration.Description, type, registration.DefaultValue);
        _runtimeBinding?.RegisterFlag(flag);
        registry.RegisterFlag(SourceId(registration.ExtensionId), flag);
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterPromptSectionAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsPromptSectionRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        var section = new PromptSection(
            registration.Id,
            PromptSectionKind.Extension,
            new MarkdownPromptContent(registration.Content),
            new PromptPlacement(registration.Slot, registration.Priority),
            new PromptSectionOptions(Protected: registration.Protected));
        registry.RegisterPromptSection(SourceId(registration.ExtensionId), section, ParseOverride(registration.Override));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterPromptTransformAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsPromptTransformRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        registry.RegisterPromptTransform(SourceId(registration.ExtensionId), new TsStaticPromptTransform(registration));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterMessageRendererAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsMessageRendererRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        var sourceId = SourceId(registration.ExtensionId);
        var extensionId = registration.ExtensionId;
        var handler = CreateMessageRenderProxy(extensionId, registration.Name);
        var rowType = ParseRowType(registration.RowType, ExtensionChatRowType.Custom);
        var rendererRegistration = new ExtensionMessageRendererRegistration(
            registration.Name,
            RowType: rowType,
            Handler: handler,
            Override: ParseOverride(registration.Override),
            CustomType: registration.CustomType ?? (registration.RowType is null ? registration.Name : null));
        var handle = registry.RegisterMessageRenderer(sourceId, rendererRegistration);
        ReplaceRegistrationHandle(_messageRendererHandles, MessageRegistrationKey(registration.ExtensionId, registration.Name), handle);
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> UnregisterMessageRendererAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsMessageRendererRegistration>(parameters)!;
        RemoveRegistrationHandle(_messageRendererHandles, MessageRegistrationKey(registration.ExtensionId, registration.Name));
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> RegisterMessageDecoratorAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsMessageDecoratorRegistration>(parameters)!;
        EnsureDescriptorSourceReplaced(registration.ExtensionId);
        var sourceId = SourceId(registration.ExtensionId);
        var extensionId = registration.ExtensionId;
        var handler = CreateMessageDecorateProxy(extensionId, registration.Name);
        var rowType = ParseRowType(registration.RowType, ExtensionChatRowType.Custom);
        var decoratorRegistration = new ExtensionMessageDecoratorRegistration(
            registration.Name,
            RowType: rowType,
            Handler: handler,
            Order: registration.Order,
            CustomType: registration.CustomType ?? (registration.RowType is null ? registration.Name : null));
        var handle = registry.RegisterMessageDecorator(sourceId, decoratorRegistration);
        ReplaceRegistrationHandle(_messageDecoratorHandles, MessageRegistrationKey(registration.ExtensionId, registration.Name), handle);
        return Task.FromResult<object?>(new { ok = true });
    }

    private Task<object?> UnregisterMessageDecoratorAsync(object parameters, CancellationToken cancellationToken)
    {
        var registration = DeserializeObject<TsMessageDecoratorRegistration>(parameters)!;
        RemoveRegistrationHandle(_messageDecoratorHandles, MessageRegistrationKey(registration.ExtensionId, registration.Name));
        return Task.FromResult<object?>(new { ok = true });
    }

    private static void ReplaceRegistrationHandle(ConcurrentDictionary<string, IDisposable> handles, string key, IDisposable handle)
    {
        if (handles.TryRemove(key, out var previous)) previous.Dispose();
        handles[key] = handle;
    }

    private static void RemoveRegistrationHandle(ConcurrentDictionary<string, IDisposable> handles, string key)
    {
        if (handles.TryRemove(key, out var handle)) handle.Dispose();
    }

    private static string MessageRegistrationKey(string extensionId, string name)
        => $"{extensionId}:{name}";

    private ExtensionMessageRenderHandler CreateMessageRenderProxy(string extensionId, string name)
    {
        return context =>
        {
            if (_runtimeBinding is not null && !_runtimeBinding.HasUi)
                return [];
            if (!_client.IsStarted)
                return [];
            try
            {
                var data = new Dictionary<string, string>();
                if (context.Metadata is not null)
                    foreach (var kv in context.Metadata) data[kv.Key] = kv.Value;
                data.TryAdd("title", context.Text);
                var request = new TsMessageRenderRequest(
                    extensionId, name,
                    context.CustomType ?? context.Text,
                    context.CustomContent ?? context.Content ?? (object)context.Text,
                    context.CustomDisplay,
                    context.CustomDetails,
                    context.IsExpanded,
                    context.Width,
                    context.Role,
                    context.Text,
                    data);
                var task = _client.RequestAsync("render_message", request, CancellationToken.None);
                var response = AgentJsonSerializer.Deserialize<TsMessageRenderResponse>(
                    task.GetAwaiter().GetResult().GetRawText());
                if (response is null)
                    return [];
                if (response.PreserveBuiltIn == true)
                    return [];
                return response.Lines.Select(line =>
                    new ExtensionChatRow(line, ExtensionChatRowKind.Custom)).ToArray();
            }
            catch
            {
                return [];
            }
        };
    }

    private ExtensionMessageDecorateHandler CreateMessageDecorateProxy(string extensionId, string name)
    {
        return (context, rows) =>
        {
            if (_runtimeBinding is not null && !_runtimeBinding.HasUi) return rows;
            if (!_client.IsStarted) return rows;
            try
            {
                var data = new Dictionary<string, string>();
                if (context.Metadata is not null)
                    foreach (var kv in context.Metadata) data[kv.Key] = kv.Value;
                var rowDtos = rows.Select(r => new TsMessageRenderRow(
                    r.Text,
                    r.Kind.ToString().ToLowerInvariant(),
                    r.Spans?.Select(s => new TsMessageRenderSpan(s.Text, s.Kind.ToString().ToLowerInvariant())).ToArray()
                )).ToArray();
                var request = new TsMessageDecorateRequest(
                    extensionId, name,
                    context.CustomType ?? context.Text,
                    context.Text, context.Role, rowDtos, data);
                var task = _client.RequestAsync("decorate_message", request, CancellationToken.None);
                var response = AgentJsonSerializer.Deserialize<TsMessageDecorateResponse>(
                    task.GetAwaiter().GetResult().GetRawText());
                if (response?.Rows is null) return rows;
                return response.Rows.Select(r =>
                    new ExtensionChatRow(
                        r.Text,
                        Enum.TryParse<ExtensionChatRowKind>(r.Kind, true, out var kind) ? kind : ExtensionChatRowKind.Normal,
                        r.Spans?.Select(s => new ExtensionChatSpan(
                            s.Text,
                            Enum.TryParse<ExtensionChatSpanKind>(s.Kind, true, out var spanKind) ? spanKind : ExtensionChatSpanKind.Text
                        )).ToArray()
                    )).ToArray();
            }
            catch
            {
                return rows;
            }
        };
    }

    private async Task<object?> RuntimeActionAsync(object parameters, CancellationToken cancellationToken)
    {
        var request = DeserializeObject<TsRuntimeActionRequest>(parameters)!;
        if (request.Action is TsBridgeRuntimeActions.ListResources or TsBridgeRuntimeActions.ReadResource)
        {
            return request.Action switch
            {
                TsBridgeRuntimeActions.ListResources => ListResourcesAsync(),
                TsBridgeRuntimeActions.ReadResource => await ReadResourceAsync(request.Payload, cancellationToken),
                _ => new JsonRpcError(-32601, $"Unknown runtime action '{request.Action}'.")
            };
        }

        if (SdkShimRuntimeDispatcher.CanHandle(request.Action))
        {
            var directResult = _sdkDispatcher.TryResolve(request.Action, out var mappedAction);
            if (directResult is not null)
                return directResult;

            if (string.IsNullOrEmpty(mappedAction))
                return new JsonRpcError(-32601, $"Unknown runtime action '{request.Action}'.");

            if (_runtimeBinding is null) return new JsonRpcError(-32000, "Extension runtime is not bound.");
            request = request with { Action = mappedAction };
        }

        if (_runtimeBinding is null) return new JsonRpcError(-32000, "Extension runtime is not bound.");
        return request.Action switch
        {
            TsBridgeRuntimeActions.GetAllSkills => new TsRuntimeActionResult(await _runtimeBinding.GetAllSkillsAsync(cancellationToken)),
            TsBridgeRuntimeActions.GetSelectedSkills => new TsRuntimeActionResult(await _runtimeBinding.GetSelectedSkillsAsync(cancellationToken)),
            TsBridgeRuntimeActions.SetSelectedSkills => await SetSelectedSkillsAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.GetFlag => new TsRuntimeActionResult(_runtimeBinding.GetFlag(GetString(request.Payload, "name") ?? string.Empty)),
            TsBridgeRuntimeActions.GetFlags => new TsRuntimeActionResult(_runtimeBinding.FlagValues),
            TsBridgeRuntimeActions.GetActiveTools => new TsRuntimeActionResult(await _runtimeBinding.GetActiveToolsAsync(cancellationToken)),
            TsBridgeRuntimeActions.GetAllTools => new TsRuntimeActionResult(await _runtimeBinding.GetAllToolsAsync(cancellationToken)),
            TsBridgeRuntimeActions.GetCommands => new TsRuntimeActionResult(await _runtimeBinding.GetCommandsAsync(cancellationToken)),
            TsBridgeRuntimeActions.WaitForIdle => await WaitForIdleAsync(cancellationToken),
            TsBridgeRuntimeActions.NewSession => await NewSessionAsync(cancellationToken),
            TsBridgeRuntimeActions.ForkSession => await ForkSessionAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.NavigateTree => await NavigateTreeAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SwitchSession => await SwitchSessionAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.IsIdle => new TsRuntimeActionResult(await _runtimeBinding.IsIdleAsync(cancellationToken)),
            TsBridgeRuntimeActions.HasPendingMessages => new TsRuntimeActionResult(await _runtimeBinding.HasPendingMessagesAsync(cancellationToken)),
            TsBridgeRuntimeActions.Compact => await CompactAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.GetSystemPrompt => new TsRuntimeActionResult(await _runtimeBinding.GetSystemPromptAsync(cancellationToken)),
            TsBridgeRuntimeActions.Abort => await AbortAsync(cancellationToken),
            TsBridgeRuntimeActions.Shutdown => await ShutdownAsync(cancellationToken),
            TsBridgeRuntimeActions.Exec => await ExecAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.GetThinkingLevel => new TsRuntimeActionResult(await _runtimeBinding.GetThinkingLevelAsync(cancellationToken)),
            TsBridgeRuntimeActions.SendMessage => await SendMessageAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SendUserMessage => await SendUserMessageAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AppendEntry => await AppendEntryAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SetEntryLabel => await SetEntryLabelAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.GetSessionName => new TsRuntimeActionResult(await _runtimeBinding.GetSessionNameAsync(cancellationToken)),
            TsBridgeRuntimeActions.SetSessionName => await SetSessionNameAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SetActiveTools => await SetActiveToolsAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SetModel => new TsRuntimeActionResult(await _runtimeBinding.SetModelAsync(DeserializePayload<PiSharp.Agent.Core.Models.ModelDescriptor>(request.Payload), cancellationToken)),
            TsBridgeRuntimeActions.SetThinkingLevel => await SetThinkingLevelAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.ReloadExtensions => await ReloadExtensionsAsync(cancellationToken),
            TsBridgeRuntimeActions.EmitEvent => await HandleEmitEventAsync(request, cancellationToken),
            TsBridgeRuntimeActions.CompleteSimple => await CompleteSimpleAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.PromptAndWait => await PromptAndWaitAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.CreateAgentSession => await CreateAgentSessionAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionPrompt => await AgentSessionPromptAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionSteer => await AgentSessionSteerAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionFollowUp => await AgentSessionFollowUpAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionAbort => await AgentSessionAbortAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionCompact => await AgentSessionCompactAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionSetModel => await AgentSessionSetModelAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionSetThinkingLevel => await AgentSessionSetThinkingLevelAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.AgentSessionDispose => await AgentSessionDisposeAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SettingsGet => await SettingsGetAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SettingsGetCore => await SettingsGetCoreAsync(request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SettingsSet => await SettingsSetAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.SettingsRemove => await SettingsRemoveAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateGet => await StateGetAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateSet => await StateSetAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateRemove => await StateRemoveAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateGetAll => await StateGetAllAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateListKeys => await StateListKeysAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateClear => await StateClearAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateGetSchemaVersion => await StateGetSchemaVersionAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateSetSchemaVersion => await StateSetSchemaVersionAsync(request.ExtensionId, request.Payload, cancellationToken),
            TsBridgeRuntimeActions.StateRegisterMigration => await StateRegisterMigrationAsync(request.Payload, cancellationToken),
            _ => new JsonRpcError(-32601, $"Unknown runtime action '{request.Action}'.")
        };
    }

    public async Task<TsCommandInvokeResult> InvokeCommandResultAsync(TsCommandInvokeRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) return new TsCommandInvokeResult(false, "TypeScript bridge is not running.", true);
        var result = await _client.RequestAsync("invoke_command", request, cancellationToken);
        return AgentJsonSerializer.Deserialize<TsCommandInvokeResult>(result.GetRawText()) ?? new TsCommandInvokeResult();
    }

    public async Task InvokeCommandAsync(string extensionId, string name, string args, CancellationToken cancellationToken = default)
        => await InvokeCommandResultAsync(new TsCommandInvokeRequest(extensionId, name, args), cancellationToken);

    public async Task<TsToolCallResult> InvokeToolAsync(TsToolCallRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) throw new InvalidOperationException("TypeScript bridge is not running.");
        _logger.LogDebug("invoke_tool: extensionId={ExtensionId} name={ToolName} toolCallId={ToolCallId}",
            request.ExtensionId, request.Name, request.ToolCallId);
        var result = await _client.RequestAsync("invoke_tool", request, cancellationToken);
        return AgentJsonSerializer.Deserialize<TsToolCallResult>(result.GetRawText()) ?? new TsToolCallResult([]);
    }

    public async Task<TsToolRenderResult?> RenderToolCallAsync(TsToolRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) throw new InvalidOperationException("TypeScript bridge is not running.");
        var result = await _client.RequestAsync("render_tool_call", request, cancellationToken);
        return AgentJsonSerializer.Deserialize<TsToolRenderResult>(result.GetRawText());
    }

    public async Task<TsToolRenderResult?> RenderToolResultAsync(TsToolRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) throw new InvalidOperationException("TypeScript bridge is not running.");
        var result = await _client.RequestAsync("render_tool_result", request, cancellationToken);
        return AgentJsonSerializer.Deserialize<TsToolRenderResult>(result.GetRawText());
    }

    public async Task<TsUiResponse> ForwardUiRequestAsync(TsUiRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ui_request: requestId={RequestId} extensionId={ExtensionId} kind={Kind} title={Title}",
            request.RequestId, request.ExtensionId, request.Kind, request.Title);
        if (_uiBridge is null) return new TsUiResponse(request.RequestId, null, true);
        var result = await _uiBridge(request);
        var response = result switch
        {
            TsUiResponse typed => typed,
            null => new TsUiResponse(request.RequestId, null, true),
            _ => DeserializeObject<TsUiResponse>(result)
            ?? new TsUiResponse(request.RequestId, null, true)
        };
        _logger.LogDebug("ui_request: completed requestId={RequestId} extensionId={ExtensionId} kind={Kind} cancelled={Cancelled}",
            request.RequestId, request.ExtensionId, request.Kind, response.Cancelled);
        return response;
    }

    private async Task<object?> ForwardUiRequestAsync(object parameters, CancellationToken cancellationToken)
    {
        var request = DeserializeObject<TsUiRequest>(parameters)!;
        if (_runtimeBinding is not null && _runtimeBinding.HasUi && request.Payload.ValueKind != JsonValueKind.Undefined)
        {
            return await _runtimeBinding.Ui.RequestAsync(new ExtensionUiRequest(request.ExtensionId, request.Kind, request.Payload), cancellationToken);
        }
        return await ForwardUiRequestAsync(request, cancellationToken);
    }

    public async Task<AssistantMessage> CompleteProviderAsync(TsProviderCallbackRequest request, CancellationToken cancellationToken = default)
    {
        if (!_client.IsStarted) throw new InvalidOperationException("TypeScript bridge is not running.");
        var result = await _client.RequestAsync("provider_callback", request, cancellationToken);
        return AgentJsonSerializer.Deserialize<AssistantMessage>(result.GetRawText()) ?? new AssistantMessage([new TextContent("TS provider callback returned no message.")], Api: request.ProviderApi, StopReason: "error", ErrorMessage: "ts_provider_empty_response");
    }

    public async Task<TsToolExecuteResult> ExecuteToolAsync(TsToolExecuteRequest request, CancellationToken cancellationToken = default)
    {
        var result = await InvokeToolAsync(new TsToolCallRequest("default", request.Name, request.Parameters, request.ToolCallId), cancellationToken);
        return new TsToolExecuteResult(result.Content, result.Details, result.Terminate, result.IsError);
    }

    private async Task<TsRuntimeActionResult> SendMessageAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");

        if (TryParseCustomMessagePayload(payload, out var customType, out var content, out var display, out var details, out var delivery, out var triggerTurn))
        {
            if (delivery is null && !triggerTurn)
            {
                if (_runtimeBinding.AppendCustomMessageEntryAsync is null)
                    return new TsRuntimeActionResult(Ok: false, Error: "Custom message append is not bound.");
                await _runtimeBinding.AppendCustomMessageEntryAsync(customType, content, display, details, cancellationToken);
                return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
            }

            await _runtimeBinding.SendMessageAsync(
                CustomMessageContent.ToCustomMessage(customType, content, display, details),
                delivery ?? ExtensionMessageDelivery.NextTurn,
                triggerTurn,
                cancellationToken);
            return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
        }

        var textContent = GetString(payload, "content") ?? GetString(payload, "message") ?? string.Empty;
        await _runtimeBinding.SendMessageAsync(AgentMessages.User(textContent), ExtensionMessageDelivery.NextTurn, false, cancellationToken);
        return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private static bool TryParseCustomMessagePayload(object? payload, out string customType, out object content, out bool display, out object? details, out ExtensionMessageDelivery? delivery, out bool triggerTurn)
    {
        customType = string.Empty;
        content = string.Empty;
        display = false;
        details = null;
        delivery = null;
        triggerTurn = false;

        var messageElement = GetPayloadValue(payload, "message");
        if (messageElement.ValueKind != JsonValueKind.Object)
            return false;

        customType = GetString(messageElement, "customType") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(customType))
            return false;

        if (!messageElement.TryGetProperty("content", out var contentElement))
            return false;
        content = contentElement.Clone();

        if (messageElement.TryGetProperty("display", out var displayElement) && displayElement.ValueKind == JsonValueKind.True)
            display = true;

        if (messageElement.TryGetProperty("details", out var detailsElement))
            details = detailsElement.Clone();

        var optionsElement = GetPayloadValue(payload, "options");
        if (optionsElement.ValueKind == JsonValueKind.Object)
        {
            if (optionsElement.TryGetProperty("triggerTurn", out var triggerElement) && triggerElement.ValueKind == JsonValueKind.True)
                triggerTurn = true;
            delivery = GetString(optionsElement, "deliverAs") switch
            {
                "steer" => ExtensionMessageDelivery.Steer,
                "followUp" => ExtensionMessageDelivery.FollowUp,
                "nextTurn" => ExtensionMessageDelivery.NextTurn,
                _ => null
            };
        }

        return true;
    }

    private async Task<TsRuntimeActionResult> SendUserMessageAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var content = GetString(payload, "content") ?? string.Empty;
        await _runtimeBinding.SendUserMessageAsync(content, ExtensionMessageDelivery.NextTurn, cancellationToken);
        var snapshot = await RuntimeSessionSnapshotAsync(cancellationToken);
        _logger.LogDebug("[diag] SendUserMessageAsync completed — session snapshot is {HasSnapshot}", snapshot is not null);
        return new TsRuntimeActionResult(new { session = snapshot });
    }

    private async Task<TsRuntimeActionResult> PromptAndWaitAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var content = GetString(payload, "content") ?? GetString(payload, "message") ?? string.Empty;
        await _runtimeBinding.SendUserMessageAsync(content, ExtensionMessageDelivery.NextTurn, cancellationToken);
        return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> CreateAgentSessionAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var options = GetPayloadValue(payload, "options");
        var result = await _runtimeBinding.CreateAgentSessionAsync(options, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionPromptAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var content = GetString(payload, "content") ?? string.Empty;
        var options = GetPayloadValue(payload, "options");
        var result = await _runtimeBinding.AgentSessionPromptAsync(sessionId, content, options, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionSteerAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var content = GetString(payload, "content") ?? string.Empty;
        var result = await _runtimeBinding.AgentSessionSteerAsync(sessionId, content, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionFollowUpAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var content = GetString(payload, "content") ?? string.Empty;
        var result = await _runtimeBinding.AgentSessionFollowUpAsync(sessionId, content, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionAbortAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var result = await _runtimeBinding.AgentSessionAbortAsync(sessionId, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionCompactAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var instructions = GetString(payload, "instructions");
        var result = await _runtimeBinding.AgentSessionCompactAsync(sessionId, instructions, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionSetModelAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var model = DeserializePayload<ModelDescriptor>(GetPayloadValue(payload, "model"));
        var result = await _runtimeBinding.AgentSessionSetModelAsync(sessionId, model, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionSetThinkingLevelAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var level = Enum.Parse<ThinkingLevel>(GetString(payload, "level") ?? GetString(payload, "thinkingLevel") ?? "Auto", true);
        var result = await _runtimeBinding.AgentSessionSetThinkingLevelAsync(sessionId, level, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> AgentSessionDisposeAsync(object? payload, CancellationToken cancellationToken)
    {
        if (_runtimeBinding is null) return new TsRuntimeActionResult(Ok: false, Error: "Extension runtime is not bound.");
        var sessionId = GetString(payload, "sessionId") ?? string.Empty;
        var result = await _runtimeBinding.AgentSessionDisposeAsync(sessionId, cancellationToken);
        return new TsRuntimeActionResult(result);
    }

    private async Task<TsRuntimeActionResult> CompleteSimpleAsync(object? payload, CancellationToken cancellationToken)
    {
        try
        {
            var model = DeserializePayload<ModelDescriptor>(GetPayloadValue(payload, "model"));
            var context = DeserializeAgentContext(GetPayloadValue(payload, "context"));
            var optionsElement = GetPayloadValue(payload, "options");
            var streamOptions = optionsElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? new AgentStreamOptions()
                : DeserializePayload<AgentStreamOptions>(optionsElement);
            var message = await PublicApi.CompleteAsync(model, context, streamOptions, cancellationToken);
            return new TsRuntimeActionResult(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Runtime action failed");
            return new TsRuntimeActionResult(Ok: false, Error: ex.Message);
        }
    }

    private async Task<TsRuntimeActionResult> WaitForIdleAsync(CancellationToken cancellationToken)
    {
        await _runtimeBinding!.WaitForIdleAsync(cancellationToken);
        return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> NewSessionAsync(CancellationToken cancellationToken)
    {
        var result = await _runtimeBinding!.NewSessionAsync(cancellationToken);
        return new TsRuntimeActionResult(new { result.Cancelled, result.Reason, result.SessionId, result.SessionFile, session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> ForkSessionAsync(object? payload, CancellationToken cancellationToken)
    {
        var result = await _runtimeBinding!.ForkSessionAsync(GetString(payload, "entryId"), GetString(payload, "position"), cancellationToken);
        return new TsRuntimeActionResult(new { result.Cancelled, result.Reason, result.SessionId, result.SessionFile, session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> NavigateTreeAsync(object? payload, CancellationToken cancellationToken)
    {
        await _runtimeBinding!.NavigateTreeAsync(GetString(payload, "targetId") ?? GetString(payload, "id") ?? string.Empty, cancellationToken);
        return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> SwitchSessionAsync(object? payload, CancellationToken cancellationToken)
    {
        var result = await _runtimeBinding!.SwitchSessionAsync(GetString(payload, "sessionPath") ?? GetString(payload, "path") ?? string.Empty, cancellationToken);
        return new TsRuntimeActionResult(new { result.Cancelled, result.Reason, result.SessionId, result.SessionFile, session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> CompactAsync(object? payload, CancellationToken cancellationToken)
    {
        await _runtimeBinding!.CompactAsync(GetString(payload, "instructions"), cancellationToken);
        return new TsRuntimeActionResult(new { session = await RuntimeSessionSnapshotAsync(cancellationToken) });
    }

    private async Task<TsRuntimeActionResult> AbortAsync(CancellationToken cancellationToken)
    {
        await _runtimeBinding!.AbortAsync(cancellationToken);
        await SignalAbortAsync();
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        await _runtimeBinding!.ShutdownAsync(cancellationToken);
        return new TsRuntimeActionResult();
    }

    private static async Task<TsRuntimeActionResult> ExecAsync(object? payload, CancellationToken cancellationToken)
    {
        var command = GetString(payload, "command") ?? string.Empty;
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-lc \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new TsRuntimeActionResult(new { exitCode = process.ExitCode, stdout, stderr, output = stdout, error = stderr });
    }

    private async Task<TsRuntimeActionResult> AppendEntryAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        await _runtimeBinding!.AppendEntryAsync(GetString(payload, "type") ?? extensionId, payload ?? new { }, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> SetEntryLabelAsync(object? payload, CancellationToken cancellationToken)
    {
        await _runtimeBinding!.SetLabelAsync(GetString(payload, "entryId") ?? string.Empty, GetString(payload, "label"), cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> SetSessionNameAsync(object? payload, CancellationToken cancellationToken)
    {
        await _runtimeBinding!.SetSessionNameAsync(GetString(payload, "name") ?? string.Empty, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> SetActiveToolsAsync(object? payload, CancellationToken cancellationToken)
    {
        var names = DeserializePayload<string[]>(GetPayloadValue(payload, "toolNames"));
        await _runtimeBinding!.SetActiveToolsAsync(names, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> SetSelectedSkillsAsync(object? payload, CancellationToken cancellationToken)
    {
        var names = DeserializePayload<string[]>(GetPayloadValue(payload, "skillNames"));
        await _runtimeBinding!.SetSelectedSkillsAsync(names, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> SetThinkingLevelAsync(object? payload, CancellationToken cancellationToken)
    {
        var level = Enum.Parse<ThinkingLevel>(GetString(payload, "level") ?? GetString(payload, "thinkingLevel") ?? "Auto", true);
        await _runtimeBinding!.SetThinkingLevelAsync(level, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<TsRuntimeActionResult> ReloadExtensionsAsync(CancellationToken cancellationToken)
    {
        await _runtimeBinding!.ReloadExtensionsAsync(cancellationToken);
        return new TsRuntimeActionResult();
    }

    private void WireEmitDelegateIfNeeded()
    {
        if (_runtimeBinding is not null)
        {
            _runtimeBinding.EmitEventAsync = async (channel, payload, ct) =>
            {
                await _client.RequestAsync("emit_event", new TsEventForward(channel, payload), ct);
            };
        }
    }

    private void WireChildSessionEventForwarding()
    {
        if (_runtimeBinding is not null)
        {
            ClearChildSessionEventForwarding();
            StartChildSessionEventBatching(_client);
            _childEventForwarder = async (sessionId, jsEvent, ct) =>
            {
                QueueChildSessionEvent(sessionId, jsEvent, ct);
                await Task.CompletedTask;
            };
            _runtimeBinding.OnChildSessionEventAsync = _childEventForwarder;
            _childEventForwardingBinding = _runtimeBinding;
        }
    }

    private void StartChildSessionEventBatching(ITsBridgeClient client)
    {
        if (_childEventBatchQueue is not null && _childEventBatchCancellation is { IsCancellationRequested: false }) return;

        var cancellation = new CancellationTokenSource();
        var queue = Channel.CreateBounded<ChildSessionEventForward>(new BoundedChannelOptions(ChildSessionEventBatchCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _childEventBatchCancellation = cancellation;
        _childEventBatchQueue = queue;
        _childEventBatchWorker = Task.Run(() => RunChildSessionEventBatchWorkerAsync(client, queue.Reader, cancellation.Token), CancellationToken.None);
    }

    private void QueueChildSessionEvent(string sessionId, object jsEvent, CancellationToken cancellationToken)
    {
        var queue = _childEventBatchQueue;
        var batchCancellation = _childEventBatchCancellation;
        if (queue is null || batchCancellation is null || batchCancellation.IsCancellationRequested) return;

        var forward = new ChildSessionEventForward(sessionId, jsEvent);
        if (queue.Writer.TryWrite(forward)) return;

        _ = WriteChildSessionEventAsync(queue.Writer, forward, batchCancellation.Token);
    }

    private static async Task WriteChildSessionEventAsync(ChannelWriter<ChildSessionEventForward> writer, ChildSessionEventForward forward, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(forward, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
        }
    }

    private Task RunChildSessionEventBatchWorkerAsync(ITsBridgeClient client, ChannelReader<ChildSessionEventForward> reader, CancellationToken cancellationToken)
        => RunBatchWorkerAsync(client, reader, _logger, cancellationToken);

    internal static async Task RunBatchWorkerAsync(ITsBridgeClient client, ChannelReader<ChildSessionEventForward> reader, ILogger logger, CancellationToken cancellationToken)
    {
        var batch = new List<ChildSessionEventForward>(ChildSessionEventBatchSize);
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                batch.Clear();
                while (batch.Count < ChildSessionEventBatchSize && reader.TryRead(out var forward))
                    batch.Add(forward);

                if (batch.Count < ChildSessionEventBatchSize)
                {
                    await Task.Delay(ChildSessionEventBatchInterval, cancellationToken);
                    while (batch.Count < ChildSessionEventBatchSize && reader.TryRead(out var forward))
                        batch.Add(forward);
                }

                try
                {
                    if (batch.Count > 0)
                        await ForwardBatchAsync(client, batch, logger, cancellationToken);
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Child session event batch worker stopping: pipe closed");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task ForwardChildSessionEventBatchAsync(ITsBridgeClient client, IReadOnlyList<ChildSessionEventForward> batch, CancellationToken cancellationToken)
        => ForwardBatchAsync(client, batch, _logger, cancellationToken);

    internal static async Task ForwardBatchAsync(ITsBridgeClient client, IReadOnlyList<ChildSessionEventForward> batch, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var group in batch.GroupBy(item => item.SessionId, StringComparer.Ordinal))
        {
            try
            {
                await client.NotifyAsync("event", new
                {
                    name = "subagents:session:events",
                    payload = new { sessionId = group.Key, events = group.Select(item => item.JsEvent).ToArray() }
                }, cancellationToken);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Child session event batch pipe closed for session {SessionId}", group.Key);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to forward child session event batch for session {SessionId}", group.Key);
            }
        }
    }

    private void StopChildSessionEventBatching()
    {
        _childEventBatchQueue?.Writer.TryComplete();
        _childEventBatchCancellation?.Cancel();
        _childEventBatchCancellation?.Dispose();
        _childEventBatchCancellation = null;
        _childEventBatchQueue = null;
        _childEventBatchWorker = null;
    }

    private async Task SignalAbortAsync()
    {
        if (!_client.IsStarted) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _client.RequestAsync("signal_abort", new { }, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send signal_abort to TypeScript bridge.");
        }
    }

    private void ClearChildSessionEventForwarding()
    {
        if (_childEventForwardingBinding is not null
            && ReferenceEquals(_childEventForwardingBinding.OnChildSessionEventAsync, _childEventForwarder))
        {
            _childEventForwardingBinding.OnChildSessionEventAsync = null;
        }
        _childEventForwardingBinding = null;
        _childEventForwarder = null;
    }

    private async Task<object?> HandleEmitEventAsync(TsRuntimeActionRequest request, CancellationToken cancellationToken)
    {
        var channel = GetString(request.Payload, "channel") ?? string.Empty;
        var payload = GetPayloadValue(request.Payload, "payload");
        foreach (var registration in registry.HandlersFor(channel))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var evt = new ExtensionEvent(channel, null!, payload);
                await registration.Value.Handler(evt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extension emit handler for {Channel} failed", channel);
                _emitDiagnostics.Add(ex);
            }
        }
        return new TsRuntimeActionResult();
    }

    private object? ListResourcesAsync()
    {
        var items = _runtimeBinding?.ResourceItems ?? [];
        return items.Select(item => new TsResourceListItem(item.Kind, item.Path)).ToArray();
    }

    private async Task<object?> ReadResourceAsync(object? payload, CancellationToken cancellationToken)
    {
        var uri = GetString(payload, "uri");
        if (string.IsNullOrEmpty(uri))
            return new TsResourceReadResult(string.Empty, Error: "URI is required.");

        var items = _runtimeBinding?.ResourceItems ?? [];
        if (!items.Any(item => string.Equals(item.Path, uri, StringComparison.Ordinal)))
            return new TsResourceReadResult(uri, Error: $"Resource '{uri}' is not in the loaded resource set.");

        if (_runtimeBinding?.ReadResourceAsync is not null)
        {
            var content = await _runtimeBinding.ReadResourceAsync(uri, cancellationToken);
            if (content is not null)
                return new TsResourceReadResult(content.Path, content.Content);
        }

        return new TsResourceReadResult(uri, Error: $"Failed to read resource '{uri}'.");
    }

    private IExtensionSettingsApi ScopedSettings(string extensionId)
        => new ExtensionScopedSettings(new ExtensionDescriptor(extensionId, extensionId, "0.0.0"), _runtimeBinding?.RuntimeSettings);

    private IExtensionStateApi ScopedState(string extensionId)
        => new ExtensionScopedState(new ExtensionDescriptor(extensionId, extensionId, "0.0.0"), _runtimeBinding?.RuntimeState);

    private static ExtensionSettingsScope ParseSettingsScope(string? scope)
        => scope switch
        {
            "global" => ExtensionSettingsScope.Global,
            "project" => ExtensionSettingsScope.Project,
            _ => ExtensionSettingsScope.Source
        };

    private static ExtensionStateScope ParseStateScope(string? scope)
        => string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) ? ExtensionStateScope.Project : ExtensionStateScope.User;

    private static int? GetInt(object? payload, string property)
    {
        var value = GetPayloadValue(payload, property);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private async Task<object?> SettingsGetAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var key = GetString(payload, "key") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'key' is required.");
        return new TsRuntimeActionResult(ScopedSettings(extensionId).Get(key));
    }

    private async Task<object?> SettingsGetCoreAsync(object? payload, CancellationToken cancellationToken)
    {
        var path = GetString(payload, "path") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'path' is required.");
        return new TsRuntimeActionResult(_runtimeBinding?.RuntimeSettings?.GetRaw(path));
    }

    private async Task<object?> SettingsSetAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var key = GetString(payload, "key") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'key' is required.");
        var scope = ParseSettingsScope(GetString(payload, "scope"));
        var valueElement = GetPayloadValue(payload, "value");
        object? value = valueElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : valueElement;
        await ScopedSettings(extensionId).SetAsync(key, value, scope, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<object?> SettingsRemoveAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var key = GetString(payload, "key") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'key' is required.");
        var scope = ParseSettingsScope(GetString(payload, "scope"));
        await ScopedSettings(extensionId).RemoveAsync(key, scope, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<object?> StateGetAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var key = GetString(payload, "key") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'key' is required.");
        var scope = ParseStateScope(GetString(payload, "scope"));
        return new TsRuntimeActionResult(await ScopedState(extensionId).GetAsync(key, scope, cancellationToken));
    }

    private async Task<object?> StateSetAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var key = GetString(payload, "key") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'key' is required.");
        var scope = ParseStateScope(GetString(payload, "scope"));
        var valueElement = GetPayloadValue(payload, "value");
        object? value = valueElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : valueElement;
        await ScopedState(extensionId).SetAsync(key, value, scope, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<object?> StateRemoveAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var key = GetString(payload, "key") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'key' is required.");
        var scope = ParseStateScope(GetString(payload, "scope"));
        await ScopedState(extensionId).RemoveAsync(key, scope, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<object?> StateGetAllAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var scope = ParseStateScope(GetString(payload, "scope"));
        return new TsRuntimeActionResult(await ScopedState(extensionId).GetAllAsync(scope, cancellationToken));
    }

    private async Task<object?> StateListKeysAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var scope = ParseStateScope(GetString(payload, "scope"));
        return new TsRuntimeActionResult(await ScopedState(extensionId).ListKeysAsync(scope, cancellationToken));
    }

    private async Task<object?> StateClearAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var scope = ParseStateScope(GetString(payload, "scope"));
        await ScopedState(extensionId).ClearAsync(scope, cancellationToken);
        return new TsRuntimeActionResult();
    }

    private async Task<object?> StateGetSchemaVersionAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var scope = ParseStateScope(GetString(payload, "scope"));
        return new TsRuntimeActionResult(await ScopedState(extensionId).GetSchemaVersionAsync(scope, cancellationToken));
    }

    private async Task<object?> StateSetSchemaVersionAsync(string extensionId, object? payload, CancellationToken cancellationToken)
    {
        var version = GetInt(payload, "version");
        if (version is null) return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'version' is required.");
        var scope = ParseStateScope(GetString(payload, "scope"));
        return new TsRuntimeActionResult(await ScopedState(extensionId).SetSchemaVersionAsync(version.Value, scope, cancellationToken));
    }

    private async Task<object?> StateRegisterMigrationAsync(object? payload, CancellationToken cancellationToken)
    {
        // TS extensions migrate manually (plan §3.7): the bridge cannot transport functions, so
        // this is a declarative acknowledgement validating the shape only.
        var fromVersion = GetInt(payload, "fromVersion");
        var toVersion = GetInt(payload, "toVersion");
        if (fromVersion is null || toVersion is null || toVersion <= fromVersion)
            return new TsRuntimeActionResult(Ok: false, Error: "Invalid params: 'fromVersion' < 'toVersion' are required.");
        return new TsRuntimeActionResult();
    }

    private static ExtensionSkillRegistration ToExtensionSkillRegistration(TsSkillRegistration registration)
        => new(registration.Name, registration.Description, registration.Content, registration.FilePath, registration.DisableModelInvocation, ParseOverride(registration.Override));

    private static ExtensionOverridePolicy ParseOverride(string? value)
        => value switch
        {
            not null when string.Equals(value, "overrideBuiltIn", StringComparison.OrdinalIgnoreCase) => ExtensionOverridePolicy.OverrideBuiltIn,
            not null when string.Equals(value, "override-built-in", StringComparison.OrdinalIgnoreCase) => ExtensionOverridePolicy.OverrideBuiltIn,
            not null when string.Equals(value, "override_built_in", StringComparison.OrdinalIgnoreCase) => ExtensionOverridePolicy.OverrideBuiltIn,
            not null when string.Equals(value, "override", StringComparison.OrdinalIgnoreCase) => ExtensionOverridePolicy.Override,
            _ => ExtensionOverridePolicy.Reject
        };

    private static ExtensionChatRowType ParseRowType(string? value, ExtensionChatRowType fallback)
        => Enum.TryParse<ExtensionChatRowType>(value, true, out var rowType) ? rowType : fallback;

    private static bool RequiresEagerActivation(TsExtensionDescriptor descriptor)
        => string.Equals(descriptor.Activation, "eager", StringComparison.OrdinalIgnoreCase)
            || descriptor.ProvidesServices is { Count: > 0 }
            || descriptor.ConsumesServices is { Count: > 0 };

    private static string SourceId(string extensionId) => $"extension:ts:{extensionId}";
    private static string ProviderSourceId(object parameters, string providerName) => $"{SourceId(ExtensionId(parameters))}:provider:{providerName}";

    private static string ExtensionId(object parameters)
    {
        return TryGetPayloadProperty(parameters, "extensionId", out var id) ? id.GetString() ?? "default" : "default";
    }

    private static string ProviderName(object parameters)
    {
        return TryGetPayloadProperty(parameters, "name", out var name) ? name.GetString() ?? "default" : "default";
    }

    private static string? GetString(object? payload, string property)
    {
        var value = GetPayloadValue(payload, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : value.ToString();
    }

    private static JsonElement GetPayloadValue(object? payload, string property)
    {
        if (payload is null) return default;
        return TryGetPayloadProperty(payload, property, out var value) ? value.Clone() : default;
    }

    private static bool TryGetPayloadProperty(object payload, string property, out JsonElement value)
    {
        if (payload is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                return element.TryGetProperty(property, out value);

            value = default;
            return false;
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var propertyValue))
        {
            value = propertyValue.Clone();
            return true;
        }

        value = default;
        return false;
    }

    private static T DeserializePayload<T>(object? payload)
    {
        return DeserializeObject<T>(payload)!;
    }

    private static T? DeserializeObject<T>(object? value)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return AgentJsonSerializer.Deserialize<T>("null");

            return AgentJsonSerializer.Deserialize<T>(element.GetRawText());
        }

        return AgentJsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    }

    private static AgentContext DeserializeAgentContext(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new AgentContext(string.Empty, []);
        var systemPrompt = element.TryGetProperty("systemPrompt", out var prompt) && prompt.ValueKind == JsonValueKind.String ? prompt.GetString() ?? string.Empty : string.Empty;
        var messages = new List<AgentMessage>();
        if (element.TryGetProperty("messages", out var messageArray) && messageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messageArray.EnumerateArray())
            {
                var role = message.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : null;
                var content = DeserializeMessageContent(message.TryGetProperty("content", out var contentElement) ? contentElement : default);
                messages.Add(string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? new AssistantMessage(content)
                    : AgentMessages.User(content));
            }
        }
        return new AgentContext(systemPrompt, messages);
    }

    private static IReadOnlyList<MessageContent> DeserializeMessageContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return [new TextContent(element.GetString() ?? string.Empty)];
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) ? new TextContent(text.GetString() ?? string.Empty) : new TextContent(part.ToString())).ToArray();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("text", out var objectText)) return [new TextContent(objectText.GetString() ?? string.Empty)];
        return [new TextContent(element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? string.Empty : element.ToString())];
    }

    private sealed class TsStaticPromptTransform(TsPromptTransformRegistration registration) : IPromptTransform
    {
        public SystemPromptDocument Apply(SystemPromptDocument document, SystemPromptCompositionContext context)
        {
            var removeIds = (registration.RemoveSectionIds ?? []).ToHashSet(StringComparer.Ordinal);
            var sections = document.Sections.Where(section => !removeIds.Contains(section.Id)).ToList();
            if (!string.IsNullOrWhiteSpace(registration.AppendMarkdown))
            {
                sections.Add(new PromptSection(
                    $"ts-transform:{registration.ExtensionId}:{registration.Name}",
                    PromptSectionKind.Extension,
                    new MarkdownPromptContent(registration.AppendMarkdown),
                    new PromptPlacement("instructions")));
            }
            return document with { Sections = sections };
        }
    }

    internal sealed record ChildSessionEventForward(string SessionId, object JsEvent);

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        ClearChildSessionEventForwarding();
        StopChildSessionEventBatching();
        await SignalAbortAsync();
        await _client.DisposeAsync();
        _disposeCts.Dispose();
        _startGate.Dispose();
        _loadGate.Dispose();
    }
}
