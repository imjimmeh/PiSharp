using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerUiResponse>> _pending = new(StringComparer.Ordinal);
    private readonly ServerSessionRegistry _registry;
    private readonly ILogger<ServerUiBridge> _logger;

    public ServerUiBridge(ServerSessionRegistry registry, ILogger<ServerUiBridge>? logger = null)
    {
        _registry = registry;
        _logger = logger ?? NullLogger<ServerUiBridge>.Instance;
    }

    public async Task<ServerUiResponse> RequestUiAsync(ServerUiIntent intent, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ServerUiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[intent.RequestId] = tcs;
        try
        {
            EmitUiRequest(intent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResponseTimeout);
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

    private void EmitUiRequest(ServerUiIntent intent)
    {
        var session = SelectSession();
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
}
