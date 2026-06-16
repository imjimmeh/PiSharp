using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Google;

internal static class GoogleStreamParser
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
        await foreach (var evt in HttpModelProvider.ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (!TryParse(evt.Data, out var root)) continue;
            foreach (var messageEvt in ReadCandidateParts(root, state)) yield return messageEvt;
            var fallbackText = root.GetPathString("text");
            foreach (var textEvt in state.AddText(fallbackText ?? string.Empty)) yield return textEvt;
            usage = ReadUsage(root.GetObj("usageMetadata"), usage);
        }
        foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
        yield return new AssistantMessageEvent.Done(state.Message("stop", usage: usage), "stop");
    }

    private static IEnumerable<AssistantMessageEvent> ReadCandidateParts(JsonElement root, TextStreamState state)
    {
        var candidates = root.GetObj("candidates");
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) yield break;
        var parts = candidates[0].GetObj("content").GetObj("parts");
        if (parts.ValueKind != JsonValueKind.Array) yield break;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("thought", out var thoughtVal) && thoughtVal.ValueKind == JsonValueKind.True)
            {
                foreach (var thinkEvt in state.AddThinking(S(part, "text") ?? string.Empty)) yield return thinkEvt;
            }
            else
            {
                foreach (var textEvt in state.AddText(S(part, "text") ?? string.Empty)) yield return textEvt;
            }

            var functionCall = part.GetObj("functionCall");
            var name = S(functionCall, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var args = functionCall.ValueKind == JsonValueKind.Object && functionCall.TryGetProperty("args", out var argsElement) ? argsElement.GetRawText() : "{}";
            foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
            foreach (var toolEvt in state.AddToolCall(Guid.NewGuid().ToString("N"), name, args)) yield return toolEvt;
        }
    }

    private static bool TryParse(string json, out JsonElement element) { try { using var doc = JsonDocument.Parse(json); element = doc.RootElement.Clone(); return true; } catch { element = default; return false; } }
    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static UsageInfo ReadUsage(JsonElement e, UsageInfo current)
    {
        if (e.ValueKind != JsonValueKind.Object) return current;
        var input = e.TryGetProperty("promptTokenCount", out var i) && i.TryGetInt32(out var iv) ? iv : current.Input;
        var output = e.TryGetProperty("candidatesTokenCount", out var o) && o.TryGetInt32(out var ov) ? ov : current.Output;
        var total = e.TryGetProperty("totalTokenCount", out var t) && t.TryGetInt32(out var tv) ? tv : input + output;
        return new UsageInfo(input, output, TotalTokens: total);
    }
}
