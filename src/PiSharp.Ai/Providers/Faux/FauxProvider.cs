using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Faux;

public sealed class FauxProvider : IModelProvider
{
    public const string DefaultApi = "faux";
    private readonly IReadOnlyList<FauxResponseItem> _items;

    public FauxProvider(IEnumerable<FauxResponseItem>? items = null, string api = DefaultApi)
    {
        Api = api;
        _items = items?.ToArray() ?? [FauxResponseItem.Text("ok")];
    }

    public string Api { get; }

    public bool WasStreamCalled { get; private set; }

    public bool WasCompleteCalled { get; private set; }

    public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WasStreamCalled = true;
        var content = new List<MessageContent>();
        var message = CreateMessage(model, content, Usage: new UsageInfo());
        yield return new AssistantMessageEvent.Start(message);

        var terminalEmitted = false;
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            switch (item)
            {
                case FauxResponseItem.TextItem text:
                    var textIndex = content.Count;
                    content.Add(new TextContent(string.Empty));
                    message = CreateMessage(model, content);
                    yield return new AssistantMessageEvent.TextStart(message, textIndex);
                    foreach (var chunk in SplitChunks(text.Value))
                    {
                        content[textIndex] = new TextContent(((TextContent)content[textIndex]).Text + chunk);
                        message = CreateMessage(model, content);
                        yield return new AssistantMessageEvent.TextDelta(message, textIndex, chunk);
                    }
                    yield return new AssistantMessageEvent.TextEnd(message, textIndex);
                    break;
                case FauxResponseItem.ThinkingItem thinking:
                    var thinkingIndex = content.Count;
                    content.Add(new ThinkingContent(string.Empty));
                    message = CreateMessage(model, content);
                    yield return new AssistantMessageEvent.ThinkingStart(message, thinkingIndex);
                    content[thinkingIndex] = new ThinkingContent(thinking.Value);
                    message = CreateMessage(model, content);
                    yield return new AssistantMessageEvent.ThinkingDelta(message, thinkingIndex, thinking.Value);
                    yield return new AssistantMessageEvent.ThinkingEnd(message, thinkingIndex);
                    break;
                case FauxResponseItem.ToolCallItem toolCall:
                    var toolIndex = content.Count;
                    message = CreateMessage(model, content);
                    yield return new AssistantMessageEvent.ToolCallStart(message, toolIndex);
                    yield return new AssistantMessageEvent.ToolCallDelta(message, toolIndex, toolCall.Arguments.GetRawText());
                    var call = new ToolCallContent(ToolTransformer.NormalizeToolCallId(toolCall.Id), toolCall.Name, toolCall.Arguments.Clone());
                    content.Add(call);
                    message = CreateMessage(model, content);
                    yield return new AssistantMessageEvent.ToolCallEnd(message, toolIndex, call);
                    break;
                case FauxResponseItem.ErrorItem error:
                    terminalEmitted = true;
                    message = CreateMessage(model, content, StopReason: "error", ErrorMessage: error.Message, Usage: BuildUsage(content));
                    yield return new AssistantMessageEvent.Error(message, "error");
                    yield break;
                case FauxResponseItem.AbortItem:
                    terminalEmitted = true;
                    message = CreateMessage(model, content, StopReason: "error", ErrorMessage: "aborted", Usage: BuildUsage(content));
                    yield return new AssistantMessageEvent.Error(message, "aborted");
                    yield break;
            }
        }

        if (!terminalEmitted)
        {
            message = CreateMessage(model, content, StopReason: "stop", Usage: BuildUsage(content));
            yield return new AssistantMessageEvent.Done(message, "stop");
        }
    }

    public async Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        WasCompleteCalled = true;
        AssistantMessage? terminal = null;
        await foreach (var evt in StreamAsync(model, context, options, cancellationToken).ConfigureAwait(false))
        {
            terminal = evt switch
            {
                AssistantMessageEvent.Done done => done.Message,
                AssistantMessageEvent.Error error => error.ErrorMessage,
                _ => terminal
            };
        }

        return terminal ?? CreateMessage(model, [], StopReason: "stop", Usage: new UsageInfo());
    }

    private static AssistantMessage CreateMessage(
        ModelDescriptor model,
        IReadOnlyList<MessageContent> content,
        string? StopReason = null,
        string? ErrorMessage = null,
        UsageInfo? Usage = null)
        => new(content.ToArray(), Api: model.Api, Provider: model.Provider, Model: model.Id, Usage: Usage, StopReason: StopReason, ErrorMessage: ErrorMessage);

    private static UsageInfo BuildUsage(IReadOnlyList<MessageContent> content)
    {
        var output = content.OfType<TextContent>().Sum(text => text.Text.Length) + content.OfType<ThinkingContent>().Sum(thinking => thinking.Thinking.Length);
        return new UsageInfo(Input: 0, Output: output, TotalTokens: output);
    }

    private static IEnumerable<string> SplitChunks(string value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        const int size = 4;
        for (var i = 0; i < value.Length; i += size) yield return value.Substring(i, Math.Min(size, value.Length - i));
    }
}

public abstract record FauxResponseItem
{
    public sealed record TextItem(string Value) : FauxResponseItem;
    public sealed record ThinkingItem(string Value) : FauxResponseItem;
    public sealed record ToolCallItem(string Id, string Name, JsonElement Arguments) : FauxResponseItem;
    public sealed record ErrorItem(string Message) : FauxResponseItem;
    public sealed record AbortItem : FauxResponseItem;

    public static FauxResponseItem Text(string value) => new TextItem(value);
    public static FauxResponseItem Thinking(string value) => new ThinkingItem(value);
    public static FauxResponseItem ToolCall(string id, string name, JsonElement arguments) => new ToolCallItem(id, name, arguments.Clone());
    public static FauxResponseItem Error(string message) => new ErrorItem(message);
    public static FauxResponseItem Abort() => new AbortItem();
}
