using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Extensions;

public sealed record ExtensionCompleteRequest(
    string Provider,             // api name, e.g. "anthropic-messages"
    string ModelId,              // within provider
    string SystemPrompt = "",    // optional; empty => none
    IReadOnlyList<AgentMessage>? Messages = null,  // null => prompt-only (CompleteSimple)
    int? MaxTokens = null,
    int? TimeoutMs = null);      // advisor watchdog uses this as its hard cap

public enum ExtensionCompletionStatus { Ok, Cancelled, Timeout, Error }

public sealed record ExtensionCompletionResult(
    ExtensionCompletionStatus Status,
    string? Text,                // non-null when Ok
    string? Error,
    UsageInfo? Usage);

public sealed record ExtensionCompletionDelta(AssistantMessageEvent Event, string? TextDelta, bool Final);

/// <summary>
/// Model-completion surface exposed to extensions via
/// <see cref="IExtensionApi.Completion"/>. Maps onto
/// <c>PiSharp.Ai.PublicApi</c> through the runtime binding delegates so
/// in-process and daemon modes behave identically.
/// </summary>
public interface IExtensionCompletionApi
{
    // Non-streaming, prompt-only convenience (maps to PublicApi.CompleteSimpleAsync).
    Task<ExtensionCompletionResult> CompleteSimpleAsync(
        string provider, string modelId, string prompt,
        ExtensionCompleteRequest? options = null,
        CancellationToken cancellationToken = default);

    // Message-list completion with optional system prompt (maps to PublicApi.CompleteAsync).
    Task<ExtensionCompletionResult> CompleteAsync(
        string provider, string modelId,
        IReadOnlyList<AgentMessage>? messages, string? systemPrompt = null,
        ExtensionCompleteRequest? options = null,
        CancellationToken cancellationToken = default);

    // Streaming completion; the caller consumes deltas and, on completion,
    // the final summary. Used by the advisor for incremental presentation.
    IAsyncEnumerable<ExtensionCompletionDelta> StreamAsync(
        string provider, string modelId,
        IReadOnlyList<AgentMessage>? messages, string? systemPrompt = null,
        ExtensionCompleteRequest? options = null,
        CancellationToken cancellationToken = default);
}
