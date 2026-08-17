using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Streaming;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Streaming;

namespace PiSharp.Agent.Loops;

public static class AgentLoop
{
    public static IEventStream<AgentEvent, IReadOnlyList<AgentMessage>> Run(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken cancellationToken = default)
    {
        var logger = config.LoggerFactory?.CreateLogger("PiSharp.Agent.Loops.AgentLoop") ?? NullLogger.Instance;
        var stream = new EventStream<AgentEvent, IReadOnlyList<AgentMessage>>();
        _ = Task.Run(async () =>
        {
            try
            {
                var messages = await RunAgentLoopAsync(prompts, context, config, stream.Push, cancellationToken);
                stream.End(messages);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Agent loop failed");
                stream.Error(exception);
            }
        }, CancellationToken.None);
        return stream;
    }

    public static IEventStream<AgentEvent, IReadOnlyList<AgentMessage>> RunContinue(
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken cancellationToken = default)
    {
        if (context.Messages.Count == 0) throw new InvalidOperationException("Cannot continue: no messages in context");
        if (context.Messages[^1] is AssistantMessage) throw new InvalidOperationException("Cannot continue from message role: assistant");

        var logger = config.LoggerFactory?.CreateLogger("PiSharp.Agent.Loops.AgentLoop") ?? NullLogger.Instance;
        var stream = new EventStream<AgentEvent, IReadOnlyList<AgentMessage>>();
        _ = Task.Run(async () =>
        {
            try
            {
                var messages = await RunAgentLoopContinueAsync(context, config, stream.Push, cancellationToken);
                stream.End(messages);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Agent loop failed");
                stream.Error(exception);
            }
        }, CancellationToken.None);
        return stream;
    }

    public static Task<IReadOnlyList<AgentMessage>> RunAgentLoopAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken = default)
        => RunAgentLoopAsync(prompts, context, config, (evt, _) =>
        {
            emit(evt);
            return Task.CompletedTask;
        }, cancellationToken);

    public static async Task<IReadOnlyList<AgentMessage>> RunAgentLoopAsync(
        IReadOnlyList<AgentMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken = default)
    {
        var newMessages = prompts.ToList();
        var currentMessages = context.Messages.Concat(prompts).ToList();
        var currentContext = context with { Messages = currentMessages };

        await emitAsync(new AgentEvent.AgentStart(), cancellationToken);
        await emitAsync(new AgentEvent.TurnStart(), cancellationToken);
        foreach (var prompt in prompts)
        {
            await emitAsync(new AgentEvent.MessageStart(prompt), cancellationToken);
            await emitAsync(new AgentEvent.MessageEnd(prompt), cancellationToken);
        }

        await RunLoopAsync(currentContext, newMessages, config, emitAsync, cancellationToken);
        return newMessages;
    }

    public static Task<IReadOnlyList<AgentMessage>> RunAgentLoopContinueAsync(
        AgentContext context,
        AgentLoopConfig config,
        Action<AgentEvent> emit,
        CancellationToken cancellationToken = default)
        => RunAgentLoopContinueAsync(context, config, (evt, _) =>
        {
            emit(evt);
            return Task.CompletedTask;
        }, cancellationToken);

    public static async Task<IReadOnlyList<AgentMessage>> RunAgentLoopContinueAsync(
        AgentContext context,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken = default)
    {
        if (context.Messages.Count == 0 || context.Messages[^1] is AssistantMessage)
        {
            throw new InvalidOperationException("Continue requires a non-assistant tail message.");
        }

        var newMessages = new List<AgentMessage>();
        var currentContext = context with { Messages = context.Messages.ToList() };
        await emitAsync(new AgentEvent.AgentStart(), cancellationToken);
        await emitAsync(new AgentEvent.TurnStart(), cancellationToken);
        await RunLoopAsync(currentContext, newMessages, config, emitAsync, cancellationToken);
        return newMessages;
    }

    private static async Task RunLoopAsync(
        AgentContext initialContext,
        List<AgentMessage> newMessages,
        AgentLoopConfig initialConfig,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        var currentContext = initialContext;
        var config = initialConfig;
        var firstTurn = true;
        var pendingMessages = new List<AgentMessage>();

        while (true)
        {
            var hasMoreToolCalls = true;
            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                if (!firstTurn) await emitAsync(new AgentEvent.TurnStart(), cancellationToken);
                firstTurn = false;

                foreach (var message in pendingMessages)
                {
                    await emitAsync(new AgentEvent.MessageStart(message), cancellationToken);
                    await emitAsync(new AgentEvent.MessageEnd(message), cancellationToken);
                    Append(currentContext, message);
                    newMessages.Add(message);
                }
                pendingMessages.Clear();

                var assistantMessage = await StreamAssistantResponseAsync(currentContext, config, emitAsync, cancellationToken);
                newMessages.Add(assistantMessage);

                if (assistantMessage.StopReason is "error" or "aborted")
                {
                    await emitAsync(new AgentEvent.TurnEnd(assistantMessage, []), cancellationToken);
                    await emitAsync(new AgentEvent.AgentEnd(newMessages.ToArray()), cancellationToken);
                    return;
                }

                var toolResults = new List<ToolResultMessage>();
                hasMoreToolCalls = false;
                if (assistantMessage.Content.OfType<ToolCallContent>().Any())
                {
                    var batch = await ToolCallExecutor.ExecuteAsync(currentContext, assistantMessage, config, emitAsync, cancellationToken);
                    toolResults.AddRange(batch.Messages);
                    hasMoreToolCalls = !batch.Terminate;
                    foreach (var result in toolResults)
                    {
                        Append(currentContext, result);
                        newMessages.Add(result);
                    }
                }

                await emitAsync(new AgentEvent.TurnEnd(assistantMessage, toolResults), cancellationToken);
                var update = config.PrepareNextTurn is null
                    ? null
                    : await config.PrepareNextTurn(new PrepareNextTurnContext(assistantMessage, toolResults, currentContext, newMessages.ToArray()), cancellationToken);
                if (update is not null)
                {
                    currentContext = update.Context ?? currentContext;
                    config = config with { Model = update.Model ?? config.Model, ThinkingLevel = update.ThinkingLevel ?? config.ThinkingLevel };
                }

                if (config.ShouldStopAfterTurn is not null && await config.ShouldStopAfterTurn(new ShouldStopAfterTurnContext(assistantMessage, toolResults, currentContext, newMessages.ToArray()), cancellationToken))
                {
                    await emitAsync(new AgentEvent.AgentEnd(newMessages.ToArray()), cancellationToken);
                    return;
                }

                pendingMessages = (await DrainAsync(config.GetSteeringMessages, cancellationToken)).ToList();
            }

            var followUps = await DrainAsync(config.GetFollowUpMessages, cancellationToken);
            if (followUps.Count > 0)
            {
                pendingMessages = followUps.ToList();
                continue;
            }
            break;
        }

        await emitAsync(new AgentEvent.AgentEnd(newMessages.ToArray()), cancellationToken);
    }

    private static async Task<AssistantMessage> StreamAssistantResponseAsync(
        AgentContext context,
        AgentLoopConfig config,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = context.Messages;
            if (config.TransformContext is not null) messages = await config.TransformContext(messages, cancellationToken);
            if (config.ConvertToLlm is not null) messages = await config.ConvertToLlm(messages, cancellationToken);

            var options = config.StreamOptions ?? new AgentStreamOptions();
            string? apiKey = options.ApiKey;
            if (config.GetApiKey is not null)
            {
                var keyTask = config.GetApiKey(config.Model.Provider, cancellationToken);
                if (keyTask is not null)
                {
                    apiKey = await keyTask ?? options.ApiKey;
                }
            }
            options = options with { ApiKey = apiKey, Reasoning = config.ThinkingLevel == Abstractions.Options.ThinkingLevel.Off ? null : config.ThinkingLevel.ToString().ToLowerInvariant() };

            var retriesUsed = 0;
            var maxRetries = Math.Max(0, config.MaxStreamRetries);
            var retryReason = string.Empty;

            async Task<AssistantMessage> FinalizeAttemptAsync(AssistantMessage message, bool wasAdded, bool succeeded, string? finalError = null)
            {
                // A retried attempt closes the retry: Done → success, Error → failure.
                if (retriesUsed > 0) await emitAsync(new AgentEvent.AutoRetryEnd(succeeded, retriesUsed, finalError), cancellationToken);
                return await FinalizeAssistantAsync(context, message, wasAdded, emitAsync, cancellationToken);
            }
            while (true)
            {
                var retryRequested = false;

                string? retryReminder = null;
                AssistantMessage? partialMessage = null;
                var addedPartial = false;

                var requestMessages = messages;
                if (config.PrepareStreamMessages is not null)
                    requestMessages = await config.PrepareStreamMessages(messages, context, cancellationToken);

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var attemptToken = attemptCts.Token;

                var enumerator = config.StreamAsync(config.Model, context with { Messages = requestMessages }, options, attemptToken)
                    .WithCancellation(attemptToken)
                    .GetAsyncEnumerator();
                await using (enumerator)
                {
                    while (await enumerator.MoveNextAsync())
                    {
                        var streamEvent = enumerator.Current;
                        switch (streamEvent)
                        {
                            case AssistantMessageEvent.Start start:
                                partialMessage = start.Partial;
                                Append(context, partialMessage);
                                addedPartial = true;
                                await emitAsync(new AgentEvent.MessageStart(partialMessage), cancellationToken);
                                break;
                            case AssistantMessageEvent.Done done:
                                return await FinalizeAttemptAsync(done.Message, addedPartial, succeeded: true);
                            case AssistantMessageEvent.Error error:
                                var errorMessage = NormalizeErrorMessage(config.Model, error.ErrorMessage);
                                return await FinalizeAttemptAsync(errorMessage, addedPartial, succeeded: false, errorMessage.ErrorMessage);
                            default:
                                var beforeDelta = partialMessage ?? streamEvent.Partial;
                                partialMessage = ApplyAssistantEvent(partialMessage, streamEvent);
                                if (addedPartial) ReplaceLast(context, partialMessage);
                                else
                                {
                                    Append(context, partialMessage);
                                    addedPartial = true;
                                    await emitAsync(new AgentEvent.MessageStart(partialMessage), cancellationToken);
                                }
                                await emitAsync(new AgentEvent.MessageUpdate(partialMessage, streamEvent), cancellationToken);

                                if (config.OnStreamDelta is null) break;
                                var decision = await config.OnStreamDelta(new StreamDeltaContext(streamEvent, beforeDelta, context), attemptToken);
                                if (decision is null || decision.Action == StreamDeltaAction.Continue) break;
                                attemptCts.Cancel();
                                if (decision.Action == StreamDeltaAction.Abort)
                                {
                                    // The error message REPLACES the aborted partial (ReplaceLast); the partial
                                    // never receives its own MessageEnd, so it is never persisted.
                                    var abortText = string.IsNullOrWhiteSpace(decision.Reason) ? "Stream aborted by an interceptor." : $"Stream aborted: {decision.Reason}";
                                    return await FinalizeAssistantAsync(context,
                                        new AssistantMessage([new TextContent(abortText)], Api: config.Model.Api, Provider: config.Model.Provider, Model: config.Model.Id, StopReason: "error", ErrorMessage: decision.Reason ?? "Stream aborted by an interceptor."),
                                        addedPartial, emitAsync, cancellationToken);
                                }
                                DiscardPartial(context, partialMessage, addedPartial);
                                retryReason = string.IsNullOrWhiteSpace(decision.Reason) ? "Stream retry requested by an interceptor." : decision.Reason;
                                retryReminder = decision.SystemReminder;
                                retryRequested = true;
                                break;
                        }
                        if (retryRequested) break;
                    }
                }

                if (!retryRequested)
                {
                    return await FinalizeAttemptAsync(partialMessage ?? new AssistantMessage([], Api: config.Model.Api, Provider: config.Model.Provider, Model: config.Model.Id), addedPartial, succeeded: true);
                }

                if (retriesUsed >= maxRetries)
                {
                    var finalError = $"Max stream retries exceeded for {retryReason}";
                    await emitAsync(new AgentEvent.AutoRetryEnd(false, retriesUsed, finalError), cancellationToken);
                    return await FinalizeAssistantAsync(context,
                        new AssistantMessage([new TextContent($"Stream aborted after {retriesUsed + 1} attempts: {finalError}")], Api: config.Model.Api, Provider: config.Model.Provider, Model: config.Model.Id, StopReason: "error", ErrorMessage: finalError),
                        addedPartial: false, emitAsync, cancellationToken);
                }
                retriesUsed++;
                await emitAsync(new AgentEvent.AutoRetryStart(retriesUsed, config.MaxStreamRetries, 0, retryReason), cancellationToken);
                if (retryReminder is not null)
                {
                    messages = [.. messages, AgentMessages.User(retryReminder)];
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            (config.LoggerFactory?.CreateLogger("PiSharp.Agent.Loops.AgentLoop") ?? NullLogger.Instance).LogWarning(exception, "Agent loop failed");
            return await FinalizeAssistantAsync(context, ProviderErrorMessage(config.Model, exception), addedPartial: false, emitAsync, cancellationToken);
        }
    }

    private static AssistantMessage ApplyAssistantEvent(AssistantMessage? current, AssistantMessageEvent streamEvent)
    {
        var message = current ?? streamEvent.Partial;
        return streamEvent switch
        {
            AssistantMessageEvent.TextStart textStart => ReplaceContent(message, textStart.ContentIndex, GetPartialContent(streamEvent.Partial, textStart.ContentIndex) ?? new TextContent(string.Empty)),
            AssistantMessageEvent.TextDelta textDelta => ApplyTextDelta(message, textDelta),
            AssistantMessageEvent.TextEnd textEnd => MergePartialContent(message, textEnd.Partial, textEnd.ContentIndex),
            AssistantMessageEvent.ThinkingStart thinkingStart => ReplaceContent(message, thinkingStart.ContentIndex, GetPartialContent(streamEvent.Partial, thinkingStart.ContentIndex) ?? new ThinkingContent(string.Empty)),
            AssistantMessageEvent.ThinkingDelta thinkingDelta => ApplyThinkingDelta(message, thinkingDelta),
            AssistantMessageEvent.ThinkingEnd thinkingEnd => MergePartialContent(message, thinkingEnd.Partial, thinkingEnd.ContentIndex),
            AssistantMessageEvent.ToolCallStart toolStart => ReplaceContent(message, toolStart.ContentIndex, GetPartialContent(streamEvent.Partial, toolStart.ContentIndex) ?? EmptyToolCall()),
            AssistantMessageEvent.ToolCallDelta toolDelta => MergePartialContent(message, toolDelta.Partial, toolDelta.ContentIndex),
            AssistantMessageEvent.ToolCallEnd toolEnd => ReplaceContent(message, toolEnd.ContentIndex, toolEnd.ToolCall),
            _ => streamEvent.Partial
        };
    }

    private static AssistantMessage ApplyTextDelta(AssistantMessage message, AssistantMessageEvent.TextDelta textDelta)
    {
        var current = GetContent(message, textDelta.ContentIndex) as TextContent;
        var partial = GetPartialContent(textDelta.Partial, textDelta.ContentIndex) as TextContent;
        if (partial is not null && CanUseCumulativePartial(current?.Text, partial.Text, textDelta.Delta))
        {
            return ReplaceContent(message, textDelta.ContentIndex, partial);
        }

        return UpdateText(message, textDelta.ContentIndex, text => text with { Text = text.Text + textDelta.Delta }, new TextContent(textDelta.Delta));
    }

    private static AssistantMessage ApplyThinkingDelta(AssistantMessage message, AssistantMessageEvent.ThinkingDelta thinkingDelta)
    {
        var current = GetContent(message, thinkingDelta.ContentIndex) as ThinkingContent;
        var partial = GetPartialContent(thinkingDelta.Partial, thinkingDelta.ContentIndex) as ThinkingContent;
        if (partial is not null && CanUseCumulativePartial(current?.Thinking, partial.Thinking, thinkingDelta.Delta))
        {
            return ReplaceContent(message, thinkingDelta.ContentIndex, partial);
        }

        return UpdateThinking(message, thinkingDelta.ContentIndex, thinking => thinking with { Thinking = thinking.Thinking + thinkingDelta.Delta }, new ThinkingContent(thinkingDelta.Delta));
    }

    private static bool CanUseCumulativePartial(string? current, string partial, string delta)
    {
        if (!partial.EndsWith(delta, StringComparison.Ordinal)) return false;
        return current is null
            ? partial.Length >= delta.Length
            : partial.Length >= current.Length + delta.Length && partial.StartsWith(current, StringComparison.Ordinal);
    }

    private static AssistantMessage UpdateText(AssistantMessage message, int contentIndex, Func<TextContent, TextContent> update, TextContent fallback)
    {
        var content = GetContent(message, contentIndex) as TextContent ?? fallback;
        return ReplaceContent(message, contentIndex, update(content));
    }

    private static AssistantMessage UpdateThinking(AssistantMessage message, int contentIndex, Func<ThinkingContent, ThinkingContent> update, ThinkingContent fallback)
    {
        var content = GetContent(message, contentIndex) as ThinkingContent ?? fallback;
        return ReplaceContent(message, contentIndex, update(content));
    }

    private static AssistantMessage MergePartialContent(AssistantMessage message, AssistantMessage partial, int contentIndex)
    {
        var content = GetPartialContent(partial, contentIndex);
        return content is null ? message : ReplaceContent(message, contentIndex, content);
    }

    private static MessageContent? GetPartialContent(AssistantMessage partial, int contentIndex)
        => contentIndex >= 0 && contentIndex < partial.Content.Count ? partial.Content[contentIndex] : null;

    private static MessageContent? GetContent(AssistantMessage message, int contentIndex)
        => contentIndex >= 0 && contentIndex < message.Content.Count ? message.Content[contentIndex] : null;

    private static AssistantMessage ReplaceContent(AssistantMessage message, int contentIndex, MessageContent content)
    {
        if (contentIndex < 0) throw new InvalidOperationException($"Invalid assistant content index {contentIndex}.");

        var nextContent = message.Content.ToList();
        while (nextContent.Count < contentIndex)
        {
            nextContent.Add(new TextContent(string.Empty));
        }

        if (nextContent.Count == contentIndex) nextContent.Add(content);
        else nextContent[contentIndex] = content;

        return message with { Content = nextContent };
    }

    private static ToolCallContent EmptyToolCall()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return new ToolCallContent(string.Empty, string.Empty, document.RootElement.Clone());
    }

    private static async Task<AssistantMessage> FinalizeAssistantAsync(
        AgentContext context,
        AssistantMessage message,
        bool addedPartial,
        Func<AgentEvent, CancellationToken, Task> emitAsync,
        CancellationToken cancellationToken)
    {
        if (addedPartial) ReplaceLast(context, message);
        else
        {
            Append(context, message);
            await emitAsync(new AgentEvent.MessageStart(message), cancellationToken);
        }
        await emitAsync(new AgentEvent.MessageEnd(message), cancellationToken);
        return message;
    }

    private static AssistantMessage NormalizeErrorMessage(ModelDescriptor model, AssistantMessage message)
    {
        if (!string.IsNullOrWhiteSpace(ContentText(message))) return message;
        var text = string.IsNullOrWhiteSpace(message.ErrorMessage) ? "Provider returned an error without details." : message.ErrorMessage;
        return message with
        {
            Api = message.Api ?? model.Api,
            Provider = message.Provider ?? model.Provider,
            Model = message.Model ?? model.Id,
            StopReason = string.IsNullOrWhiteSpace(message.StopReason) ? "error" : message.StopReason,
            ErrorMessage = string.IsNullOrWhiteSpace(message.ErrorMessage) ? text : message.ErrorMessage,
            Content = [new TextContent(text)]
        };
    }

    private static AssistantMessage ProviderErrorMessage(ModelDescriptor model, Exception exception)
    {
        var detail = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
        var text = $"Provider error for {model.Provider}/{model.Id} ({model.Api}): {detail}";
        return new AssistantMessage([new TextContent(text)], Api: model.Api, Provider: model.Provider, Model: model.Id, StopReason: "error", ErrorMessage: detail);
    }

    private static string ContentText(AssistantMessage message)
        => string.Concat(message.Content.Select(content => content switch
        {
            TextContent text => text.Text,
            ThinkingContent thinking => thinking.Redacted ? "[redacted thinking]" : thinking.Thinking,
            ToolCallContent tool => $"[tool: {tool.Name}]",
            ImageContent image => $"[image: {image.MediaType}]",
            _ => string.Empty
        }));

    private static async Task<IReadOnlyList<AgentMessage>> DrainAsync(Func<CancellationToken, Task<IReadOnlyList<AgentMessage>>>? drain, CancellationToken cancellationToken)
        => drain is null ? [] : await drain(cancellationToken);

    private static void Append(AgentContext context, AgentMessage message)
    {
        if (context.Messages is List<AgentMessage> list) list.Add(message);
        else throw new InvalidOperationException("AgentLoop requires a mutable context message list.");
    }

    private static void ReplaceLast(AgentContext context, AgentMessage message)
    {
        if (context.Messages is List<AgentMessage> list && list.Count > 0) list[^1] = message;
        else throw new InvalidOperationException("AgentLoop requires a mutable context message list.");
    }

    private static void DiscardPartial(AgentContext context, AssistantMessage partialMessage, bool addedPartial)
    {
        if (!addedPartial || partialMessage is null) return;
        if (context.Messages is List<AgentMessage> list && list.Count > 0 && ReferenceEquals(list[^1], partialMessage))
            list.RemoveAt(list.Count - 1);
    }
}
