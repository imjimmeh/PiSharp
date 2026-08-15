using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Agent.Core.Events;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;

namespace PiSharp.Server.UiBridge;

/// <summary>
/// Default <see cref="IServerUiBridge"/>: pending requests are tracked in a concurrent dictionary,
/// pushed to the target session as <c>ui_request</c> flat events via
/// <see cref="LiveServerSession.EmitEvent"/>, and completed by <see cref="ResolveUiAsync"/>. If no
/// client answers within <see cref="ResponseTimeout"/>, the request auto-resolves as cancelled.
/// </summary>
public sealed class ServerUiBridge : IServerUiBridge
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Default window for interactive kinds answered by a human over the wire: generous so
    /// a dialog that takes a while to answer is not auto-cancelled server-side.</summary>
    internal static readonly TimeSpan InteractiveKindTimeout = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> InteractiveKinds = new(StringComparer.Ordinal)
    {
        "select", "input", "editor", "confirm", "custom", "custom_update"
    };

    /// <summary>Per-kind default response timeout: interactive kinds get <see cref="InteractiveKindTimeout"/>,
    /// everything else keeps the bridge's short <see cref="ResponseTimeout"/>.</summary>
    internal static TimeSpan TimeoutFor(string kind)
        => InteractiveKinds.Contains(kind) ? InteractiveKindTimeout : ResponseTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerUiResponse>> _pending = new(StringComparer.Ordinal);
    private readonly ServerSessionRegistry _registry;
    private readonly ILogger<ServerUiBridge> _logger;
    private readonly ThemeRegistry _themes;

    public ServerUiBridge(ServerSessionRegistry registry, ILogger<ServerUiBridge>? logger = null, ThemeRegistry? themeRegistry = null)
    {
        _registry = registry;
        _logger = logger ?? NullLogger<ServerUiBridge>.Instance;
        _themes = themeRegistry ?? new ThemeRegistry();
    }

    public Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, CancellationToken cancellationToken = default)
        => RequestUiAsyncCore(intent, target: null, responseTimeout: null, cancellationToken);

    public Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, LiveServerSession target, TimeSpan? responseTimeout, CancellationToken ct = default)
        => RequestUiAsyncCore(intent, target, responseTimeout, ct);

    /// <summary>
    /// Shared request pipeline: registers a pending response keyed by the intent's request id, emits
    /// the <c>ui_request</c> onto <paramref name="target"/> (falling back to the most recent session
    /// when null), and auto-cancels after <paramref name="responseTimeout"/> (the bridge default when
    /// null) unless a client resolves it first.
    /// </summary>
    private async Task<ServerUiResponse> RequestUiAsyncCore(ServerUiIntent intent, LiveServerSession? target, TimeSpan? responseTimeout, CancellationToken cancellationToken)
    {
        // Theme UI kinds are answered daemon-side from the ThemeRegistry (plan C8) — never
        // forwarded to an attached client, because themes are daemon-resident.
        var intercepted = await TryInterceptThemeRequestAsync(intent, cancellationToken);
        if (intercepted is not null) return intercepted;

        var tcs = new TaskCompletionSource<ServerUiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[intent.RequestId] = tcs;
        try
        {
            EmitUiRequest(intent, target);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(responseTimeout ?? TimeoutFor(intent.Kind));
            using var registration = timeout.Token.Register(() => tcs.TrySetResult(new ServerUiResponse(intent.RequestId, null, Cancelled: true)));
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ServerUiResponse(intent.RequestId, null, Cancelled: true);
            }
        }
        finally
        {
            _pending.TryRemove(intent.RequestId, out _);
        }
    }

    public void ResolveUiAsync(string requestId, string? value, bool cancelled)
    {
        if (_pending.TryGetValue(requestId, out var tcs)) tcs.TrySetResult(new ServerUiResponse(requestId, value, cancelled));
    }

    private void EmitUiRequest(ServerUiIntent intent, LiveServerSession? target)
    {
        var session = target ?? SelectSession();
        if (session is null)
        {
            _logger.LogDebug("UI request {RequestId} has no target session; treating as cancelled", intent.RequestId);
            return;
        }

        session.EmitEvent(AgentSessionEvent.FromServer("ui_request", new
        {
            requestId = intent.RequestId,
            kind = intent.Kind,
            title = intent.Title,
            message = intent.Message,
            options = intent.Options,
            component = intent.Component,
            extensionId = intent.ExtensionId
        }));
    }

    /// <summary>
    /// Targets the most recently created live session: server session ids are
    /// <c>srv_&lt;version-7-uuid&gt;</c> values, so the lexicographically greatest id is the newest
    /// session. A session-scoped overload is added when the CLI host wires extension UI through a
    /// specific session's binding.
    /// </summary>
    private LiveServerSession? SelectSession() => _registry.Sessions.MaxBy(session => session.Id, StringComparer.Ordinal);

    /// <summary>
    /// Answers theme UI kinds daemon-side (plan C8) or returns <c>null</c> so the request falls
    /// through to the client round-trip. <c>get_all_themes</c> and <c>get_theme</c> respond from
    /// the registry; <c>set_theme</c> activates the named theme, applies it to every live runtime
    /// and broadcasts <c>theme_changed</c> exactly like the <c>set_theme</c> command.
    /// </summary>
    private async Task<ServerUiResponse?> TryInterceptThemeRequestAsync(ServerUiIntent intent, CancellationToken cancellationToken)
    {
        switch (intent.Kind)
        {
            case "get_all_themes":
                return new ServerUiResponse(
                    intent.RequestId,
                    _themes.Documents
                        .Select(document => new { name = document.Name, document })
                        .ToArray());
            case "get_theme":
                var active = _themes.ActiveDocument;
                return new ServerUiResponse(
                    intent.RequestId,
                    active is null ? null : new { name = active.Name, document = active });
            case "set_theme":
                var name = ExtractThemeName(intent);
                if (name is null || !_themes.TrySetActive(name))
                    return new ServerUiResponse(intent.RequestId, null, Cancelled: true);
                await ThemeRegistry.ApplyToSessionsAsync(_themes, _registry.Sessions, name, cancellationToken);
                return new ServerUiResponse(intent.RequestId);
            default:
                return null;
        }
    }

    /// <summary>
    /// Extracts the theme name from a <c>set_theme</c> intent: the structured <c>Component</c>
    /// payload's <c>name</c> or <c>theme</c> property (TS-bridge parity sends <c>{ theme }</c>),
    /// falling back to the raw <c>Message</c> as the plain name.
    /// </summary>
    private static string? ExtractThemeName(ServerUiIntent intent)
    {
        if (intent.Component is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "name", "theme" })
            {
                if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            }
        }

        return string.IsNullOrWhiteSpace(intent.Message) ? null : intent.Message;
    }
}
