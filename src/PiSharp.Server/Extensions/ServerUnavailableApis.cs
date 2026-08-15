using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;

namespace PiSharp.Server.Extensions;

/// <summary>
/// No-op <see cref="IExtensionSessionApi"/> for daemon-side shortcut invocation: the daemon
/// forwarder owns the live session, so out-of-band session mutation from a shortcut is not
/// supported. Mirrors the TUI's <c>TuiUnavailableApis</c> (internal to PiSharp.Tui).
/// </summary>
internal sealed class UnavailableExtensionSessionApi : IExtensionSessionApi
{
    public static UnavailableExtensionSessionApi Instance { get; } = new();

    private UnavailableExtensionSessionApi()
    {
    }

    public Task SendMessageAsync(AgentMessage message, ExtensionMessageDelivery delivery, bool triggerTurn = false, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SendUserMessageAsync(string content, ExtensionMessageDelivery delivery = ExtensionMessageDelivery.FollowUp, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task AppendEntryAsync(string customType, object data, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<string?> GetNameAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetNameAsync(string name, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// No-op <see cref="IExtensionToolApi"/> for daemon-side shortcut invocation (mirrors the TUI's
/// <c>UnavailableExtensionToolApi</c>).
/// </summary>
internal sealed class UnavailableExtensionToolApi : IExtensionToolApi
{
    public static UnavailableExtensionToolApi Instance { get; } = new();

    private UnavailableExtensionToolApi()
    {
    }

    public IDisposable RegisterTool(ExtensionToolRegistration registration)
        => NullDisposable.Instance;

    public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task SetActiveToolsAsync(IReadOnlyList<string> toolNames, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// No-op <see cref="IExtensionModelApi"/> for daemon-side shortcut invocation (mirrors the TUI's
/// <c>UnavailableExtensionModelApi</c>).
/// </summary>
internal sealed class UnavailableExtensionModelApi : IExtensionModelApi
{
    public static UnavailableExtensionModelApi Instance { get; } = new();

    private UnavailableExtensionModelApi()
    {
    }

    public Task<bool> SetModelAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<ThinkingLevel?> GetThinkingLevelAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ThinkingLevel?>(null);

    public Task SetThinkingLevelAsync(ThinkingLevel level, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class NullDisposable : IDisposable
{
    public static NullDisposable Instance { get; } = new();

    private NullDisposable()
    {
    }

    public void Dispose()
    {
    }
}
