using PiSharp.Extensions;

namespace PiSharp.Git.Tests;

/// <summary>In-memory IExtensionUi used by slash-command tests.</summary>
internal sealed class FakeUi : IExtensionUi
{
    public bool ConfirmResult { get; set; } = true;
    public string? InputResult { get; set; }
    public int ConfirmCalls { get; private set; }
    public List<(string Message, ExtensionUiSeverity Severity)> Notifications { get; } = [];
    public List<string> InputPrompts { get; } = [];

    public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken = default)
    {
        ConfirmCalls++;
        return Task.FromResult(ConfirmResult);
    }

    public Task<string?> InputAsync(string prompt, string? initialValue = null, CancellationToken cancellationToken = default)
    {
        InputPrompts.Add(prompt);
        return Task.FromResult(InputResult);
    }

    public Task NotifyAsync(string message, ExtensionUiSeverity severity = ExtensionUiSeverity.Info, CancellationToken cancellationToken = default)
    {
        Notifications.Add((message, severity));
        return Task.CompletedTask;
    }

    public Task<string?> SelectAsync(string prompt, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetStatusAsync(string extensionId, string? status, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetWidgetAsync(string extensionId, ExtensionWidgetState? widget, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
