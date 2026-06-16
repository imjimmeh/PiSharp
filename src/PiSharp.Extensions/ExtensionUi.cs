using System.Text.Json;

namespace PiSharp.Extensions;

public interface IExtensionUi
{
    Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<ExtensionUiResult>(new NotSupportedException("Extension UI is not available in this mode."));
    Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default);
    Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default);
    Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default);
    IDisposable OnTerminalInput(Func<string, ExtensionTerminalInputResult?> handler) => new DisposableAction(() => { });
    Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default);
    Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default);
    Task SetTitleAsync(string extensionId, string? title, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<string?> GetEditorTextAsync(string extensionId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task SetEditorTextAsync(string extensionId, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetWorkingMessageAsync(string? message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetWorkingVisibleAsync(bool visible, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetWorkingIndicatorAsync(ExtensionWorkingIndicator? indicator, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetHiddenThinkingLabelAsync(string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetFooterAsync(string extensionId, ExtensionWidgetState? footer, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetHeaderAsync(string extensionId, ExtensionWidgetState? header, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task RegisterMenuItemAsync(string extensionId, ExtensionMenuItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task ShowCustomAsync(string extensionId, ExtensionWidgetState component, CancellationToken cancellationToken = default) => SetWidgetAsync(extensionId, component, cancellationToken);
    Task PasteToEditorAsync(string extensionId, string text, CancellationToken cancellationToken = default) => SetEditorTextAsync(extensionId, text, cancellationToken);
    Task<string?> OpenEditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) => InputAsync(title, prefill, cancellationToken);
    IDisposable AddAutocompleteProvider(string extensionId, Func<string, IReadOnlyList<string>> provider) => new DisposableAction(() => { });
}

public enum ExtensionUiSeverity { Info, Success, Warning, Error }
public sealed record ExtensionTerminalInputResult(bool Consume = false, string? Data = null);
public sealed record ExtensionWorkingIndicator(string? Message = null, bool? Visible = null, string? Spinner = null);
public sealed record ExtensionUiRequest(string ExtensionId, string Kind, JsonElement Payload);
public sealed record ExtensionUiResult(bool Ok, object? Value = null, string? Error = null);
public sealed record ExtensionMenuItem(string Menu, string Label, string Command, string? Shortcut = null);

public sealed class NoExtensionUi : IExtensionUi
{
    public static NoExtensionUi Instance { get; } = new();
    private NoExtensionUi() { }

    public Task<ExtensionUiResult> RequestAsync(ExtensionUiRequest request, CancellationToken cancellationToken = default) => Unsupported<ExtensionUiResult>();
    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default) => Unsupported();
    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default) => Unsupported<bool>();
    public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default) => Unsupported<string?>();
    public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default) => Unsupported<string?>();
    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default) => Unsupported();
    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default) => Unsupported();

    private static Task Unsupported() => Task.FromException(new NotSupportedException("Extension UI is not available in this mode."));
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("Extension UI is not available in this mode."));
}

internal sealed class DisposableAction(Action dispose) : IDisposable
{
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
    }
}
