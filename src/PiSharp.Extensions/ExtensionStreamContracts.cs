using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Extensions;

/// <summary>
/// Generic mid-stream interceptor; the rules engine is the first (and v1 only)
/// implementation. Registry collection: "stream-delta:{sourceId}". First non-null
/// decision wins; null → <see cref="StreamDeltaAction.Continue"/>.
/// </summary>
public interface IStreamDeltaInterceptor
{
    Task<StreamDeltaDecision?> InterceptDeltaAsync(StreamDeltaContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs before EVERY stream request (first attempt and each retry): sticky
    /// RULES.md injection lives here. Must return the full new message list.
    /// </summary>
    Task<IReadOnlyList<AgentMessage>> PrepareMessagesAsync(
        IReadOnlyList<AgentMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
