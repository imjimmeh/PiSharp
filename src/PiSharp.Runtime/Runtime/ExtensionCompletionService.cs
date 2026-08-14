using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai;
using PiSharp.Extensions;

namespace PiSharp.Runtime;

/// <summary>
/// Wraps <see cref="PublicApi"/> for the extension completion surface
/// (<see cref="IExtensionCompletionApi"/>). Resolves models through
/// <see cref="PublicApi.ResolveCatalogModel"/>, applies the
/// <see cref="ExtensionCompleteRequest.TimeoutMs"/> cap via a linked
/// cancellation source, and classifies outcomes into
/// <see cref="ExtensionCompletionStatus"/>.
/// </summary>
internal sealed class ExtensionCompletionService
{
    public async Task<ExtensionCompletionResult> CompleteSimpleAsync(
        string provider, string modelId, string prompt,
        ExtensionCompleteRequest? options, CancellationToken cancellationToken)
    {
        using var timeoutSource = ApplyTimeout(options, cancellationToken);
        try
        {
            var message = await PublicApi.CompleteSimpleAsync(provider, modelId, prompt, ToAgentStreamOptions(options), timeoutSource?.Token ?? cancellationToken).ConfigureAwait(false);
            return ToResult(ExtensionCompletionStatus.Ok, message, null);
        }
        catch (OperationCanceledException) when (TimedOut(timeoutSource, cancellationToken))
        {
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Timeout, null, $"Completion timed out after {options?.TimeoutMs} ms.", null);
        }
        catch (OperationCanceledException)
        {
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Cancelled, null, null, null);
        }
        catch (Exception exception)
        {
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Error, null, exception.Message, null);
        }
    }

    public async Task<ExtensionCompletionResult> CompleteAsync(
        string provider, string modelId,
        IReadOnlyList<AgentMessage>? messages, string? systemPrompt,
        ExtensionCompleteRequest? options, bool streamFullOnTimeout, CancellationToken cancellationToken)
    {
        // "stream full" mode ignores the watchdog cap and runs to completion.
        using var timeoutSource = streamFullOnTimeout ? null : ApplyTimeout(options, cancellationToken);
        try
        {
            var model = PublicApi.ResolveCatalogModel(provider, modelId);
            var message = await PublicApi.CompleteAsync(model, ToContext(options, messages, systemPrompt), ToAgentStreamOptions(options), timeoutSource?.Token ?? cancellationToken).ConfigureAwait(false);
            return ToResult(ExtensionCompletionStatus.Ok, message, null);
        }
        catch (OperationCanceledException) when (TimedOut(timeoutSource, cancellationToken))
        {
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Timeout, null, $"Completion timed out after {options?.TimeoutMs} ms.", null);
        }
        catch (OperationCanceledException)
        {
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Cancelled, null, null, null);
        }
        catch (Exception exception)
        {
            return new ExtensionCompletionResult(ExtensionCompletionStatus.Error, null, exception.Message, null);
        }
    }

    public async IAsyncEnumerable<ExtensionCompletionDelta> StreamAsync(
        string provider, string modelId,
        IReadOnlyList<AgentMessage>? messages, string? systemPrompt,
        ExtensionCompleteRequest? options, bool streamFullOnTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeoutSource = streamFullOnTimeout ? null : ApplyTimeout(options, cancellationToken);
        var model = PublicApi.ResolveCatalogModel(provider, modelId);
        await foreach (var evt in PublicApi.StreamAsync(model, ToContext(options, messages, systemPrompt), ToAgentStreamOptions(options), timeoutSource?.Token ?? cancellationToken).ConfigureAwait(false))
        {
            yield return new ExtensionCompletionDelta(evt, evt is AssistantMessageEvent.TextDelta text ? text.Delta : null, evt is AssistantMessageEvent.Done);
        }
    }

    private static AgentContext ToContext(ExtensionCompleteRequest? options, IReadOnlyList<AgentMessage>? messages, string? systemPrompt)
        => new(systemPrompt ?? options?.SystemPrompt ?? string.Empty, messages ?? [], null);

    private static AgentStreamOptions? ToAgentStreamOptions(ExtensionCompleteRequest? options)
        => options is null ? null : new AgentStreamOptions { MaxTokens = options.MaxTokens };

    private static CancellationTokenSource? ApplyTimeout(ExtensionCompleteRequest? options, CancellationToken cancellationToken)
    {
        if (options?.TimeoutMs is not > 0) return null;
        var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.TimeoutMs.Value);
        return timeoutSource;
    }

    private static bool TimedOut(CancellationTokenSource? timeoutSource, CancellationToken cancellationToken)
        => timeoutSource is not null && timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    private static ExtensionCompletionResult ToResult(ExtensionCompletionStatus status, AssistantMessage message, string? error)
        => new(status, ExtractText(message), error, message.Usage);

    private static string? ExtractText(AssistantMessage message)
    {
        var text = string.Join("\n", message.Content.OfType<TextContent>().Select(content => content.Text));
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
