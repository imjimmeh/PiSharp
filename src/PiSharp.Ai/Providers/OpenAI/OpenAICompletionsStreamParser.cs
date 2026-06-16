using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.OpenAI;

internal static class OpenAICompletionsStreamParser
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

        var toolCalls = new ToolCallDeltaAssembler();
        await foreach (var evt in HttpModelProvider.ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (evt.Data == "[DONE]") break;
            if (!TryParse(evt.Data, out var root)) continue;
            var choices = Obj(root, "choices");
            var choice = choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 ? choices[0] : default;
            if (choice.ValueKind != JsonValueKind.Object) continue;
            var delta = Obj(choice, "delta");
            foreach (var thinkEvt in state.AddThinking(S(delta, "reasoning_content") ?? string.Empty)) yield return thinkEvt;
            foreach (var textEvt in state.AddText(S(delta, "content") ?? string.Empty)) yield return textEvt;
            toolCalls.AccumulateOpenAIChatDelta(delta);
            var finish = S(choice, "finish_reason");
            if (!string.IsNullOrWhiteSpace(finish))
            {
                foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                foreach (var toolEvt in toolCalls.Emit(state)) yield return toolEvt;
                var reason = StopReasonMapper.Map(finish) ?? "stop";
                yield return new AssistantMessageEvent.Done(state.Message(reason), reason);
                yield break;
            }
        }

        foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
        yield return new AssistantMessageEvent.Done(state.Message("stop"), "stop");
    }

    private static bool TryParse(string json, out JsonElement element) { try { using var doc = JsonDocument.Parse(json); element = doc.RootElement.Clone(); return true; } catch { element = default; return false; } }
    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Obj(JsonElement e, string n)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)) return v;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
