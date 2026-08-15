using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Extensions;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed record ExtensionUiIntent(string RequestId, string Kind, string Title, string? Message, IReadOnlyList<string>? Options, object? Component, string? ExtensionId = null);
public sealed record ExtensionUiIntentResult(string RequestId, object? Value, bool Cancelled = false);

public sealed class ExtensionUiBridgeHost(Window window, Action<Func<TuiRenderState, TuiRenderState>>? updateState = null, Func<string?>? getEditorText = null, Action<string>? setEditorText = null, Func<TuiRenderState>? getState = null, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger<ExtensionUiBridgeHost> _logger = loggerFactory?.CreateLogger<ExtensionUiBridgeHost>() ?? NullLogger<ExtensionUiBridgeHost>.Instance;
    private readonly object _customUiSessionGate = new();
    private readonly SemaphoreSlim _customUiInputForwardingGate = new(1, 1);
    private ExtensionCustomUiOverlay? _customUiOverlay;
    private TaskCompletionSource<ExtensionUiResult>? _customUiCompletion;
    public Window Window { get; } = window;
    public bool HasActiveCustomUi => _customUiOverlay is not null;
    internal ExtensionCustomUiOverlay? CustomUiOverlay => _customUiOverlay;
    public Action? RestoreFocus { get; set; }
    internal Action<Action>? DispatchUi { get; set; }
    internal Action<string>? ShowNotification { get; set; }
    public Func<string, string?, int?, int?, string?, CancellationToken, Task<ExtensionCustomUiSnapshot>>? SendCustomUiInputAsync { get; set; }
    public Func<string, IReadOnlyList<string>?, CancellationToken, Task<string?>>? SelectAction { get; set; }
    public Func<string, string?, CancellationToken, Task<string?>>? InputAction { get; set; }
    public Func<string, string?, CancellationToken, Task<bool>>? ConfirmAction { get; set; }
    /// <summary>Renders a permission approval and returns the user's verdict as a string value
    /// (<c>"allow"</c>/<c>"deny"</c>), matching how the daemon's <c>permission_request</c> lane is
    /// consumed on the server. With no action the request auto-cancels (the safe deny).</summary>
    public Func<string, string?, CancellationToken, Task<string?>>? ApprovalAction { get; set; }
    private Action<Action> UiPost => DispatchUi ?? TerminalGuiDispatcher.Instance.Post;

    private void InvokeOnUiThread(Action action)
        => UiPost(action);

    private async Task InvokeOnUiThreadAsync(Action action, CancellationToken cancellationToken = default)
    {
        try
        {
            await TuiDispatcherExtensions.InvokeAsync(UiPost, action, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Extension UI dispatch failed");
            throw;
        }
    }

    private async Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        try
        {
            var innerTask = await TuiDispatcherExtensions.InvokeAsync(UiPost, action, cancellationToken).ConfigureAwait(false);
            return await innerTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Extension UI dispatch failed");
            throw;
        }
    }

    private static Task<T> InvokeWithoutSynchronizationContext<T>(Func<Task<T>> action)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    public async Task NotifyAsync(string message, CancellationToken cancellationToken = default)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            (ShowNotification ?? (text => MessageBox.Query("Pi Extension", text, "OK")))(message);
            RestoreFocus?.Invoke();
        }, cancellationToken);
    }

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetExtensionStatus(extensionId, status)), cancellationToken);

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() =>
        {
            updateState?.Invoke(state => widget is null
                ? state.RemoveBridgeSlot($"widget:{extensionId}")
                : state.UpsertBridgeSlot(new TuiBridgeSlot(
                    $"widget:{extensionId}",
                    widget.Kind,
                    widget.Title ?? extensionId,
                    widget.Content,
                    Placement: widget.Placement,
                    SourceId: extensionId)));
        }, cancellationToken);

    public Task SetFooterAsync(string extensionId, ExtensionWidgetState? footer, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() =>
        {
            updateState?.Invoke(state => footer is null
                ? state.RemoveBridgeSlot($"footer:{extensionId}")
                : state.UpsertBridgeSlot(new TuiBridgeSlot(
                    $"footer:{extensionId}",
                    footer.Kind,
                    footer.Title ?? extensionId,
                    footer.Content,
                    Placement: "footer",
                    SourceId: extensionId)));
        }, cancellationToken);

    public Task SetHeaderAsync(string extensionId, ExtensionWidgetState? header, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() =>
        {
            updateState?.Invoke(state => header is null
                ? state.RemoveBridgeSlot($"header:{extensionId}")
                : state.UpsertBridgeSlot(new TuiBridgeSlot(
                    $"header:{extensionId}",
                    header.Kind,
                    header.Title ?? extensionId,
                    header.Content,
                    Placement: "header",
                    SourceId: extensionId)));
        }, cancellationToken);

    public Task RegisterMenuItemAsync(string extensionId, ExtensionMenuItem item, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.AddCustomMenuEntry(
            new TuiMenuEntry(item.Menu, item.Label, item.Command, item.Shortcut, extensionId))), cancellationToken);

    public Task SetTitleAsync(string? title, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetTitle(title)), cancellationToken);

    public Task SetWorkingMessageAsync(string? message, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetWorkingMessage(message)), cancellationToken);

    public Task SetWorkingVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetWorkingVisible(visible)), cancellationToken);

    public Task SetWorkingIndicatorAsync(ExtensionWorkingIndicator? indicator, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetWorkingIndicator(indicator)), cancellationToken);

    public async Task<ExtensionUiResult> ShowCustomComponentAsync(string extensionId, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var snapshot = CreateSnapshot(payload);

        var completion = new TaskCompletionSource<ExtensionUiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_customUiSessionGate)
        {
            if (_customUiCompletion is not null)
                throw new InvalidOperationException("A custom UI session is already active.");

            _customUiCompletion = completion;
        }

        try
        {
            await InvokeOnUiThreadAsync(() =>
            {
                updateState?.Invoke(state => state.UpsertBridgeSlot(new TuiBridgeSlot(
                    $"custom:{snapshot.RequestId}",
                    "custom",
                    snapshot.RequestId,
                    string.Join(Environment.NewLine, snapshot.Lines),
                    Visible: false,
                    SourceId: extensionId)));

                _customUiOverlay ??= CreateCustomUiOverlay();
                _customUiOverlay.SourceId = extensionId;
                BindCustomUiInputForwarding(_customUiOverlay, cancellationToken);
                _customUiOverlay.UpdateSnapshot(snapshot);
                _customUiOverlay.SetFocus();

                if (snapshot.Completed)
                {
                    CloseCustomUiOverlay(_customUiOverlay);
                    CompleteCustomUiSession(new ExtensionUiResult(snapshot.Error is null, snapshot.Value, snapshot.Error));
                    RestoreFocus?.Invoke();
                }
            }, cancellationToken)
            .ConfigureAwait(false);
        }
        catch
        {
            ClearCustomUiSession();
            throw;
        }

        using var cancellationRegistration = cancellationToken.Register(static state =>
            ((ExtensionUiBridgeHost)state!).CancelCustomUiSession("Custom UI was cancelled."), this);

        return await completion.Task.ConfigureAwait(false);
    }

    public async Task<ExtensionUiResult> UpdateCustomComponentAsync(string extensionId, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var snapshot = CreateSnapshot(payload);

        var overlay = _customUiOverlay;
        if (overlay is null || !StringComparer.Ordinal.Equals(overlay.RequestId, snapshot.RequestId))
            return new ExtensionUiResult(false, Error: "Custom UI was closed.");

        var updateApplied = false;
        await InvokeOnUiThreadAsync(() =>
        {
            if (!ReferenceEquals(_customUiOverlay, overlay)) return;

            updateState?.Invoke(state => state.UpsertBridgeSlot(new TuiBridgeSlot(
                $"custom:{snapshot.RequestId}",
                "custom",
                snapshot.RequestId,
                string.Join(Environment.NewLine, snapshot.Lines),
                Visible: false,
                SourceId: extensionId)));

            overlay.UpdateSnapshot(snapshot);
            updateApplied = true;
        }, cancellationToken).ConfigureAwait(false);

        return updateApplied
            ? new ExtensionUiResult(true)
            : new ExtensionUiResult(false, Error: "Custom UI was closed.");
    }

    public Task<string?> GetEditorTextAsync(CancellationToken cancellationToken = default)
        => TuiDispatcherExtensions.InvokeAsync(UiPost, () => getEditorText?.Invoke(), cancellationToken);

    public Task SetEditorTextAsync(string text, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() =>
        {
            setEditorText?.Invoke(text);
            updateState?.Invoke(state => state.SetEditorText(text));
        }, cancellationToken);

    public Task<bool> GetToolsExpandedAsync(CancellationToken cancellationToken = default)
        => TuiDispatcherExtensions.InvokeAsync(UiPost, () => getState?.Invoke()?.ShowToolOutput ?? false, cancellationToken);

    public Task SetToolsExpandedAsync(bool expanded, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetToolOutput(expanded)), cancellationToken);

    public Task SetEditorComponentAsync(string extensionId, ExtensionWidgetState? component, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() => updateState?.Invoke(state => state.SetEditorComponent(extensionId, component)), cancellationToken);

    public Task<ExtensionWidgetState?> GetEditorComponentAsync(string extensionId, CancellationToken cancellationToken = default)
        => TuiDispatcherExtensions.InvokeAsync(UiPost, () =>
        {
            var state = getState?.Invoke();
            var slot = state?.BridgeSlots.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, $"editor:{extensionId}"));
            return slot is null ? null : new ExtensionWidgetState(slot.Kind, slot.Content, slot.Title, "editor");
        }, cancellationToken);

    public Task ClearSourceAsync(string sourceId, CancellationToken cancellationToken = default)
        => InvokeOnUiThreadAsync(() =>
        {
            updateState?.Invoke(state => state
                .RemoveBridgeSlotsBySource(sourceId)
                .RemoveCustomMenuEntriesBySource(sourceId)
                .SetExtensionStatus(sourceId, null));

            if (_customUiOverlay is null || !StringComparer.Ordinal.Equals(_customUiOverlay.SourceId, sourceId)) return;

            Window.Remove(_customUiOverlay);
            _customUiOverlay.Visible = false;
            _customUiOverlay.ForwardInput = null;
            _customUiOverlay.ForwardResize = null;
            _customUiOverlay = null;
            CompleteCustomUiSession(new ExtensionUiResult(false, Error: "Custom UI was closed."));
            RestoreFocus?.Invoke();
        }, cancellationToken);

    public Task<ExtensionUiIntentResult> HandleAsync(ExtensionUiIntent intent, CancellationToken cancellationToken = default)
        => intent.Kind switch
        {
            "notify" => Notify(intent, cancellationToken),
            "select" => Select(intent, cancellationToken),
            "confirm" => Confirm(intent, cancellationToken),
            "input" => Input(intent, cancellationToken),
            "editor" => Input(intent, cancellationToken),
            "permission_request" => Approval(intent, cancellationToken),
            "status" => Status(intent, cancellationToken),
            "title" => Title(intent, cancellationToken),
            "working_message" => WorkingMessage(intent, cancellationToken),
            "working_visible" => WorkingVisible(intent, cancellationToken),
            "working_indicator" => WorkingIndicator(intent, cancellationToken),
            "register_menu_item" => RegisterMenuItem(intent, cancellationToken),
            "editor_get_text" => EditorGetText(intent, cancellationToken),
            "editor_set_text" => EditorSetText(intent, cancellationToken),
            "tools_expanded_get" => ToolsExpandedGet(intent, cancellationToken),
            "tools_expanded_set" => ToolsExpandedSet(intent, cancellationToken),
            "editor_component_set" => EditorComponentSet(intent, cancellationToken),
            "editor_component_get" => EditorComponentGet(intent, cancellationToken),
            "widget" => Widget(intent, cancellationToken),
            "footer" or "setFooter" => Footer(intent, cancellationToken),
            "header" or "setHeader" => Header(intent, cancellationToken),
            "custom" => Custom(intent, cancellationToken),
            _ => Task.FromResult(new ExtensionUiIntentResult(intent.RequestId, null, true))
        };

    private async Task<ExtensionUiIntentResult> Notify(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await NotifyAsync(intent.Message ?? intent.Title, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> Select(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var selectAction = SelectAction;
        if (selectAction is null)
            return new ExtensionUiIntentResult(intent.RequestId, intent.Options?.FirstOrDefault(), intent.Options is null || intent.Options.Count == 0);
        var value = await InvokeOnUiThreadAsync(() => selectAction(intent.Title, intent.Options, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new ExtensionUiIntentResult(intent.RequestId, value, value is null);
    }

    private async Task<ExtensionUiIntentResult> Confirm(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var confirmAction = ConfirmAction;
        if (confirmAction is null)
            return new ExtensionUiIntentResult(intent.RequestId, true);
        var confirmed = await InvokeOnUiThreadAsync(() => confirmAction(intent.Title, intent.Message, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new ExtensionUiIntentResult(intent.RequestId, confirmed, !confirmed);
    }

    private async Task<ExtensionUiIntentResult> Approval(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var approvalAction = ApprovalAction;
        if (approvalAction is null)
            return new ExtensionUiIntentResult(intent.RequestId, null, Cancelled: true);

        var verdict = await InvokeOnUiThreadAsync(
            () => approvalAction(ApprovalTitle(intent), ApprovalMessage(intent), cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new ExtensionUiIntentResult(intent.RequestId, verdict, verdict is null);
    }

    private static string ApprovalTitle(ExtensionUiIntent intent)
        => string.IsNullOrWhiteSpace(intent.Title) ? "Permission Request" : intent.Title;

    private static string? ApprovalMessage(ExtensionUiIntent intent)
    {
        if (intent.Component is JsonElement { ValueKind: JsonValueKind.Object } component)
        {
            var reason = GetString(component, "reason");
            var tool = GetString(component, "tool");
            if (reason is not null || tool is not null)
            {
                var lines = new List<string>(2);
                if (reason is not null) lines.Add(reason);
                if (tool is not null) lines.Add($"Tool: {tool}");
                return string.Join(Environment.NewLine, lines);
            }
        }

        return intent.Message ?? intent.Title;
    }

    private async Task<ExtensionUiIntentResult> Input(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var inputAction = InputAction;
        if (inputAction is null)
            return new ExtensionUiIntentResult(intent.RequestId, intent.Message ?? string.Empty);
        var value = await InvokeOnUiThreadAsync(() => inputAction(intent.Message ?? intent.Title, intent.Message, cancellationToken), cancellationToken).ConfigureAwait(false);
        return new ExtensionUiIntentResult(intent.RequestId, value, value is null);
    }

    private async Task<ExtensionUiIntentResult> Status(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetStatusAsync(intent.ExtensionId ?? "extension", intent.Message, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> Widget(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetWidgetAsync(intent.ExtensionId ?? "extension", new ExtensionWidgetState("custom", intent.Message ?? intent.Title, intent.Title), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> Footer(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetFooterAsync(intent.ExtensionId ?? "extension", IntentWidget(intent), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> Header(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetHeaderAsync(intent.ExtensionId ?? "extension", IntentWidget(intent), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private static ExtensionWidgetState? IntentWidget(ExtensionUiIntent intent)
    {
        var content = intent.Component?.ToString() ?? intent.Message;
        if (content is null) return null;
        return new ExtensionWidgetState("text", content, intent.Title);
    }

    private async Task<ExtensionUiIntentResult> Title(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetTitleAsync(GetPayloadString(intent, "title") ?? intent.Message ?? intent.Title, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> WorkingMessage(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetWorkingMessageAsync(intent.Message, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> WorkingVisible(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetWorkingVisibleAsync(GetPayloadBool(intent, "visible"), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> WorkingIndicator(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetWorkingIndicatorAsync(IntentWorkingIndicator(intent), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> RegisterMenuItem(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await RegisterMenuItemAsync(intent.ExtensionId ?? string.Empty, IntentMenuItem(intent), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> EditorGetText(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var text = await GetEditorTextAsync(cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, text, text is null);
    }

    private async Task<ExtensionUiIntentResult> EditorSetText(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetEditorTextAsync(GetPayloadString(intent, "text") ?? intent.Message ?? string.Empty, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> ToolsExpandedGet(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var expanded = await GetToolsExpandedAsync(cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, expanded);
    }

    private async Task<ExtensionUiIntentResult> ToolsExpandedSet(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetToolsExpandedAsync(GetPayloadBool(intent, "expanded"), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> EditorComponentSet(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await SetEditorComponentAsync(intent.ExtensionId ?? string.Empty, IntentEditorComponent(intent), cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    private async Task<ExtensionUiIntentResult> EditorComponentGet(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        var component = await GetEditorComponentAsync(intent.ExtensionId ?? string.Empty, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, component, component is null);
    }

    private static JsonElement? PayloadElement(ExtensionUiIntent intent)
        => intent.Component is JsonElement element && element.ValueKind == JsonValueKind.Object ? element : null;

    private static string? GetPayloadString(ExtensionUiIntent intent, string property)
        => PayloadElement(intent) is { } payload ? GetString(payload, property) : null;

    private static bool GetPayloadBool(ExtensionUiIntent intent, string property)
        => PayloadElement(intent) is { } payload ? GetBoolean(payload, property) : false;

    private static ExtensionWorkingIndicator? IntentWorkingIndicator(ExtensionUiIntent intent)
    {
        if (PayloadElement(intent) is not { } payload || !payload.TryGetProperty("indicator", out var indicator) || indicator.ValueKind != JsonValueKind.Object)
            return null;

        return new ExtensionWorkingIndicator(
            GetString(indicator, "message"),
            GetNullableBoolean(indicator, "visible"),
            GetString(indicator, "spinner"));
    }

    private static ExtensionMenuItem IntentMenuItem(ExtensionUiIntent intent)
    {
        var payload = PayloadElement(intent);
        var menu = payload is null ? null : GetString(payload.Value, "menu");
        var label = payload is null ? null : GetString(payload.Value, "label");
        var command = payload is null ? null : GetString(payload.Value, "command");
        var shortcut = payload is null ? null : GetString(payload.Value, "shortcut");
        return new ExtensionMenuItem(
            menu ?? "Extensions",
            label ?? intent.ExtensionId ?? string.Empty,
            command ?? string.Empty,
            shortcut);
    }

    private static ExtensionWidgetState? IntentEditorComponent(ExtensionUiIntent intent)
    {
        if (PayloadElement(intent) is not { } payload) return null;
        var content = GetString(payload, "component") ?? GetString(payload, "message");
        if (content is null) return null;
        return new ExtensionWidgetState("text", content, GetString(payload, "title"));
    }

    private async Task<ExtensionUiIntentResult> Custom(ExtensionUiIntent intent, CancellationToken cancellationToken)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            updateState?.Invoke(state => state.UpsertBridgeSlot(new TuiBridgeSlot(
                $"custom:{intent.RequestId}", "custom", intent.Title,
                intent.Component?.ToString() ?? intent.Message ?? string.Empty,
                SourceId: intent.ExtensionId)));
        }, cancellationToken);
        return new ExtensionUiIntentResult(intent.RequestId, true);
    }

    internal bool TryHandleCustomUiKey(Key key)
    {
        if (_customUiOverlay is null)
        {
            _logger.LogDebug("Custom UI key ignored because no overlay is active key={Key}", DescribeKey(key));
            return false;
        }

        var requestId = _customUiOverlay.RequestId;
        var inputDescription = ExtensionCustomUiInputTranslator.TryTranslate(key, out var translatedData)
            ? DescribeInputData(translatedData)
            : "untranslatable";

        _logger.LogDebug(
            "Custom UI key received requestId={RequestId} key={Key} input={Input}",
            requestId, DescribeKey(key), inputDescription);

        if (!_customUiOverlay.HandleKeyDown(key))
        {
            _logger.LogDebug(
                "Custom UI key not handled requestId={RequestId} key={Key} input={Input}",
                requestId, DescribeKey(key), inputDescription);
            return false;
        }

        key.Handled = true;
        _logger.LogDebug(
            "Custom UI key queued requestId={RequestId} key={Key} input={Input}",
            requestId, DescribeKey(key), inputDescription);
        return true;
    }

    private async Task ForwardCustomUiInputAsync(string data, CancellationToken cancellationToken)
    {
        await _customUiInputForwardingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ExtensionCustomUiOverlay? overlay = null;
        // Capture the completion TCS before the async gap.  After awaiting
        // SendCustomUiInputAsync, the extension may have already started a new
        // session (fast loop), replacing _customUiCompletion with a new TCS.
        // We must resolve THIS session's TCS — not any replacement.
        TaskCompletionSource<ExtensionUiResult>? capturedCompletion = null;

        try
        {
            overlay = _customUiOverlay;
            var snapshot = overlay?.Snapshot;
            lock (_customUiSessionGate)
            {
                capturedCompletion = _customUiCompletion;
            }

            if (overlay is null || snapshot is null || SendCustomUiInputAsync is null || capturedCompletion is null)
            {
                _logger.LogDebug(
                    "Custom UI input dropped before forwarding input={Input} overlayActive={OverlayActive} hasSnapshot={HasSnapshot} hasSender={HasSender} hasCompletion={HasCompletion}",
                    DescribeInputData(data), overlay is not null, snapshot is not null, SendCustomUiInputAsync is not null, capturedCompletion is not null);
                return;
            }

            _logger.LogDebug(
                "Custom UI input forwarding requestId={RequestId} input={Input} width={Width} height={Height}",
                snapshot.RequestId, DescribeInputData(data), snapshot.Width, snapshot.Height);

            var updatedSnapshot = await InvokeWithoutSynchronizationContext(() =>
                    SendCustomUiInputAsync(snapshot.RequestId, data, snapshot.Width, snapshot.Height, "input", cancellationToken))
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Custom UI input forwarded requestId={RequestId} input={Input} completed={Completed} hasError={HasError} lineCount={LineCount} width={Width} height={Height}",
                updatedSnapshot.RequestId, DescribeInputData(data), updatedSnapshot.Completed, updatedSnapshot.Error is not null,
                updatedSnapshot.Lines.Count, updatedSnapshot.Width, updatedSnapshot.Height);

            await InvokeOnUiThreadAsync(() =>
            {
                var overlayIsCurrent = ReferenceEquals(_customUiOverlay, overlay);

                // Always update the snapshot if our overlay is still current.
                if (overlayIsCurrent)
                    overlay.UpdateSnapshot(updatedSnapshot);

                if (!updatedSnapshot.Completed) return;

                // The input completed this session.  Resolve the captured TCS so that the
                // original ui_request RPC gets its response even if a new session has since
                // replaced _customUiOverlay and _customUiCompletion.
                if (overlayIsCurrent)
                    CloseCustomUiOverlay(overlay);

                // Clear _customUiCompletion only if it still refers to this session's TCS.
                lock (_customUiSessionGate)
                {
                    if (ReferenceEquals(_customUiCompletion, capturedCompletion))
                        _customUiCompletion = null;
                }

                capturedCompletion.TrySetResult(new ExtensionUiResult(updatedSnapshot.Error is null, updatedSnapshot.Value, updatedSnapshot.Error));

                // Restore focus only if we actually closed our overlay (not a replacement).
                if (overlayIsCurrent)
                    RestoreFocus?.Invoke();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Custom UI input handling failed.");

            // Resolve the captured session with an error (capturedCompletion is non-null here
            // because early-return via the guard does not throw).
            if (capturedCompletion is not null)
            {
                lock (_customUiSessionGate)
                {
                    if (ReferenceEquals(_customUiCompletion, capturedCompletion))
                        _customUiCompletion = null;
                }

                capturedCompletion.TrySetResult(new ExtensionUiResult(false, Error: ex.Message));
            }

            try
            {
                await InvokeOnUiThreadAsync(() =>
                {
                    if (overlay is null) return;

                    CloseCustomUiOverlay(overlay);
                    RestoreFocus?.Invoke();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "Custom UI cleanup failed after input handling error.");
                if (overlay is not null)
                    CloseCustomUiOverlay(overlay);
                RestoreFocus?.Invoke();
            }
        }
        finally
        {
            _customUiInputForwardingGate.Release();
        }
    }

    private async Task ForwardCustomUiResizeAsync(int width, int height, CancellationToken cancellationToken)
    {
        var overlay = _customUiOverlay;
        var snapshot = overlay?.Snapshot;
        if (overlay is null || snapshot is null || SendCustomUiInputAsync is null || _customUiCompletion is null) return;

        try
        {
            var updatedSnapshot = await InvokeWithoutSynchronizationContext(() =>
                    SendCustomUiInputAsync(snapshot.RequestId, null, width, height, "resize", cancellationToken))
                .ConfigureAwait(false);

            await InvokeOnUiThreadAsync(() =>
            {
                if (!ReferenceEquals(_customUiOverlay, overlay)) return;

                overlay.UpdateSnapshot(updatedSnapshot);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Custom UI resize handling failed.");
        }
    }

    private void QueueCustomUiInputForwarding(string data, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Custom UI input queued input={Input}", DescribeInputData(data));
        var task = ForwardCustomUiInputAsync(data, cancellationToken);
        _ = task.ContinueWith(
            continuation => _logger.LogWarning(continuation.Exception, "Custom UI input task faulted unexpectedly."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void QueueCustomUiResizeForwarding(int width, int height, CancellationToken cancellationToken)
    {
        var task = ForwardCustomUiResizeAsync(width, height, cancellationToken);
        _ = task.ContinueWith(
            continuation => _logger.LogWarning(continuation.Exception, "Custom UI resize task faulted unexpectedly."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteCustomUiSession(ExtensionUiResult result)
    {
        TaskCompletionSource<ExtensionUiResult>? completion;
        lock (_customUiSessionGate)
        {
            completion = _customUiCompletion;
            _customUiCompletion = null;
        }

        completion?.TrySetResult(result);
    }

    private void CancelCustomUiSession(string error)
    {
        var overlay = _customUiOverlay;
        if (overlay is null)
        {
            CompleteCustomUiSession(new ExtensionUiResult(false, Error: error));
            return;
        }

        var task = InvokeOnUiThreadAsync(() =>
        {
            if (ReferenceEquals(_customUiOverlay, overlay))
                CloseCustomUiOverlay(overlay);

            CompleteCustomUiSession(new ExtensionUiResult(false, Error: error));
            RestoreFocus?.Invoke();
        }, CancellationToken.None);

        _ = task.ContinueWith(
            continuation => _logger.LogWarning(continuation.Exception, "Custom UI cancellation cleanup failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ClearCustomUiSession()
    {
        lock (_customUiSessionGate)
        {
            _customUiCompletion = null;
        }
    }

    private void CloseCustomUiOverlay(ExtensionCustomUiOverlay overlay)
    {
        if (!ReferenceEquals(_customUiOverlay, overlay)) return;

        if (overlay.Snapshot is not null)
        {
            updateState?.Invoke(state => state.RemoveBridgeSlot($"custom:{overlay.Snapshot.RequestId}"));
        }

        Window.Remove(overlay);
        overlay.Visible = false;
        overlay.ForwardInput = null;
        overlay.ForwardResize = null;
        _customUiOverlay = null;
    }

    private static string? GetString(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(property, out var value)
           && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : false;

    private static bool? GetNullableBoolean(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(property, out var value)
           && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static object? GetValue(JsonElement payload, string property)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value)
            ? value.Deserialize<object?>()
            : null;

    private static IReadOnlyList<string> GetLines(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return [];

        return lines.EnumerateArray()
            .Where(line => line.ValueKind == JsonValueKind.String)
            .Select(line => line.GetString() ?? string.Empty)
            .ToArray();
    }

    private ExtensionCustomUiOverlay CreateCustomUiOverlay()
    {
        var overlay = new ExtensionCustomUiOverlay
        {
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(90),
            Height = Dim.Percent(70),
            Visible = true
        };

        Window.Add(overlay);
        return overlay;
    }

    private void BindCustomUiInputForwarding(ExtensionCustomUiOverlay overlay, CancellationToken cancellationToken)
    {
        overlay.ForwardInput = input => QueueCustomUiInputForwarding(input, cancellationToken);
        overlay.ForwardResize = (width, height) => QueueCustomUiResizeForwarding(width, height, cancellationToken);
    }

    private static ExtensionCustomUiSnapshot CreateSnapshot(JsonElement payload)
    {
        var requestId = GetString(payload, "requestId") ?? Guid.NewGuid().ToString("N");
        return new ExtensionCustomUiSnapshot(
            requestId,
            GetLines(payload),
            GetInt32(payload, "width", 80),
            GetInt32(payload, "height", 24),
            GetBoolean(payload, "completed"),
            GetValue(payload, "value"),
            GetString(payload, "error"));
    }

    private static int GetInt32(JsonElement payload, string property, int defaultValue)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : defaultValue;

    private static string DescribeKey(Key key)
        => $"KeyCode={key.KeyCode}, Ctrl={key.IsCtrl}, Alt={key.IsAlt}, Shift={key.IsShift}, Handled={key.Handled}";

    private static string DescribeInputData(string? data)
    {
        if (data is null) return "null";

        return data switch
        {
            "\u001b[A" => "arrow-up",
            "\u001b[B" => "arrow-down",
            "\u001b[C" => "arrow-right",
            "\u001b[D" => "arrow-left",
            "\r" => "enter-cr",
            "\n" => "enter-lf",
            "\u001b" => "escape",
            "\t" => "tab",
            "\u001b[Z" => "shift-tab",
            "\u007f" => "backspace",
            " " => "space",
            _ when data.StartsWith("\u001b[<", StringComparison.Ordinal) => $"mouse-sgr length={data.Length}",
            _ when data.Length == 1 && char.IsControl(data[0]) => $"control U+{(int)data[0]:X4}",
            _ when data.Any(char.IsControl) => $"control-sequence length={data.Length}",
            _ => $"printable length={data.Length}"
        };
    }
}
