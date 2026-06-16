using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Anthropic;

internal static class AnthropicStreamParser
{
    public static async IAsyncEnumerable<AssistantMessageEvent> ParseAsync(ModelDescriptor model, HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new TextStreamState(model);
        yield return new AssistantMessageEvent.Start(state.Message());
        if (!response.IsSuccessStatusCode)
        {
            yield return new AssistantMessageEvent.Error(state.Message("error", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)), "error");
            yield break;
        }

        var usage = new UsageInfo();
        string? stopReason = null;
        string currentBlockType = string.Empty;
        string toolId = string.Empty;
        string toolName = string.Empty;
        var toolArguments = string.Empty;
        await foreach (var evt in HttpModelProvider.ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (evt.Data == "[DONE]") break;
            var json = JsonElementOrNull(evt.Data);
            if (json is null) continue;
            var root = json.Value;
            var type = GetString(root, "type") ?? evt.Event;
            switch (type)
            {
                case "message_start":
                    usage = ReadUsage(root.GetPropertyOrDefault("message").GetPropertyOrDefault("usage"), usage);
                    break;
                case "content_block_start":
                    var block = root.GetPropertyOrDefault("content_block");
                    currentBlockType = GetString(block, "type") ?? string.Empty;
                    if (currentBlockType == "tool_use")
                    {
                        toolId = GetString(block, "id") ?? Guid.NewGuid().ToString("N");
                        toolName = GetString(block, "name") ?? "tool";
                        toolArguments = "";
                    }
                    else if (currentBlockType == "thinking")
                    {
                        foreach (var thinkEvt in state.AddThinking(GetString(block, "thinking") ?? string.Empty)) yield return thinkEvt;
                    }
                    break;
                case "content_block_delta":
                    var delta = root.GetPropertyOrDefault("delta");
                    var deltaType = GetString(delta, "type");
                    if (deltaType == "thinking_delta")
                    {
                        foreach (var thinkEvt in state.AddThinking(GetString(delta, "thinking") ?? string.Empty)) yield return thinkEvt;
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        toolArguments += GetString(delta, "partial_json") ?? string.Empty;
                    }
                    else
                    {
                        foreach (var textEvt in state.AddText(GetString(delta, "text") ?? string.Empty)) yield return textEvt;
                    }
                    break;
                case "content_block_stop":
                    foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                    foreach (var thinkEvt in state.EndThinkingIfOpen()) yield return thinkEvt;
                    currentBlockType = string.Empty;
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        foreach (var toolEvt in state.AddToolCall(toolId, toolName, string.IsNullOrWhiteSpace(toolArguments) ? "{}" : toolArguments)) yield return toolEvt;
                        toolId = toolName = toolArguments = string.Empty;
                    }
                    break;
                case "message_delta":
                    stopReason = StopReasonMapper.Map(GetString(root.GetPropertyOrDefault("delta"), "stop_reason"));
                    usage = ReadUsage(root.GetPropertyOrDefault("usage"), usage);
                    break;
                case "message_stop":
                    foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                    foreach (var thinkEvt in state.EndThinkingIfOpen()) yield return thinkEvt;
                    yield return new AssistantMessageEvent.Done(state.Message(stopReason ?? "stop", usage: usage), stopReason ?? "stop");
                    yield break;
            }
        }

        foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
        foreach (var thinkEvt in state.EndThinkingIfOpen()) yield return thinkEvt;
        yield return new AssistantMessageEvent.Done(state.Message(stopReason ?? "stop", usage: usage), stopReason ?? "stop");
    }

    private static JsonElement? JsonElementOrNull(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static UsageInfo ReadUsage(JsonElement element, UsageInfo current)
    {
        if (element.ValueKind != JsonValueKind.Object) return current;
        var input = element.TryGetProperty("input_tokens", out var inputTokens) && inputTokens.TryGetInt32(out var i) ? i : current.Input;
        var output = element.TryGetProperty("output_tokens", out var outputTokens) && outputTokens.TryGetInt32(out var o) ? o : current.Output;
        var cacheRead = element.TryGetProperty("cache_read_input_tokens", out var cacheReadTokens) && cacheReadTokens.TryGetInt32(out var cr) ? cr : current.CacheRead;
        var cacheWrite = element.TryGetProperty("cache_creation_input_tokens", out var cacheWriteTokens) && cacheWriteTokens.TryGetInt32(out var cw) ? cw : current.CacheWrite;
        return new UsageInfo(input, output, cacheRead, cacheWrite, input + output + cacheRead + cacheWrite);
    }
}
