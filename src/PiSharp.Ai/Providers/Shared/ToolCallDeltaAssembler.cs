using System.Text.Json;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Ai.Providers.Shared;

internal sealed class ToolCallDeltaAssembler
{
    private readonly Dictionary<int, PendingToolCall> _toolCalls = [];

    public void AccumulateOpenAIChatDelta(JsonElement delta)
    {
        var calls = Obj(delta, "tool_calls");
        if (calls.ValueKind != JsonValueKind.Array) return;
        for (var i = 0; i < calls.GetArrayLength(); i++)
        {
            var item = calls[i];
            var index = I(item, "index") ?? i;
            if (!_toolCalls.TryGetValue(index, out var pending)) _toolCalls[index] = pending = new PendingToolCall();
            pending.Id ??= S(item, "id");
            var function = Obj(item, "function");
            pending.Name ??= S(function, "name");
            pending.Arguments += S(function, "arguments") ?? string.Empty;
        }
    }

    public IEnumerable<AssistantMessageEvent> Emit(TextStreamState state)
    {
        foreach (var call in _toolCalls.OrderBy(entry => entry.Key).Select(entry => entry.Value))
        {
            if (string.IsNullOrWhiteSpace(call.Name)) continue;
            foreach (var toolEvt in state.AddToolCall(string.IsNullOrWhiteSpace(call.Id) ? Guid.NewGuid().ToString("N") : call.Id, call.Name, string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments)) yield return toolEvt;
        }
        _toolCalls.Clear();
    }

    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? I(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.TryGetInt32(out var value) ? value : null;
    private static JsonElement Obj(JsonElement e, string n)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)) return v;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private sealed class PendingToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string Arguments { get; set; } = string.Empty;
    }
}
