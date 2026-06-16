using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Mistral;

public sealed class MistralProvider : HttpModelProvider
{
    public const string ApiName = "mistral-conversations";

    public MistralProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        : base(ApiName, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        using var request = CreateJsonRequest(HttpMethod.Post, new Uri($"{BaseUrl(model)}/v1/chat/completions"), payload, credentials);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);

        var state = new TextStreamState(model);
        yield return new AssistantMessageEvent.Start(state.Message());
        if (!response.IsSuccessStatusCode)
        {
            yield return new AssistantMessageEvent.Error(state.Message("error", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)), "error");
            yield break;
        }

        var toolCalls = new Dictionary<int, PendingToolCall>();
        await foreach (var evt in ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (evt.Data == "[DONE]") break;
            if (!TryParse(evt.Data, out var root)) continue;
            var choice = root.GetPropertyOrDefault("choices").ValueKind == JsonValueKind.Array ? root.GetProperty("choices")[0] : default;
            if (choice.ValueKind != JsonValueKind.Object) continue;
            var delta = choice.GetPropertyOrDefault("delta");
            foreach (var thinkEvt in state.AddThinking(S(delta, "reasoning_content") ?? string.Empty)) yield return thinkEvt;
            foreach (var textEvt in state.AddText(S(delta, "content") ?? string.Empty)) yield return textEvt;
            AccumulateToolCalls(delta, toolCalls);
            var finish = S(choice, "finish_reason");
            if (!string.IsNullOrWhiteSpace(finish))
            {
                foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                foreach (var toolEvt in EmitToolCalls(state, toolCalls)) yield return toolEvt;
                var reason = StopReasonMapper.Map(finish) ?? "stop";
                yield return new AssistantMessageEvent.Done(state.Message(reason), reason);
                yield break;
            }
        }

        foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
        yield return new AssistantMessageEvent.Done(state.Message("stop"), "stop");
    }

    private static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
    {
        var tools = ToolTransformer.ToProviderTools(context.Tools);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.Id);
            writer.WriteBoolean("stream", true);
            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, MessageTransformer.ToProviderMessages(context, supportsImages: false, flattenThinking: true), ProviderJson.Options);
            if (options.MaxTokens is not null) writer.WriteNumber("max_tokens", options.MaxTokens.Value);
            if (options.Temperature is not null) writer.WriteNumber("temperature", options.Temperature.Value);
            ProviderToolSerializer.WriteMistralTools(writer, tools);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static IEnumerable<AssistantMessageEvent> EmitToolCalls(TextStreamState state, Dictionary<int, PendingToolCall> toolCalls)
    {
        foreach (var call in toolCalls.OrderBy(entry => entry.Key).Select(entry => entry.Value))
        {
            if (string.IsNullOrWhiteSpace(call.Name)) continue;
            foreach (var toolEvt in state.AddToolCall(string.IsNullOrWhiteSpace(call.Id) ? Guid.NewGuid().ToString("N") : call.Id, call.Name, string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments)) yield return toolEvt;
        }
        toolCalls.Clear();
    }

    private static void AccumulateToolCalls(JsonElement delta, Dictionary<int, PendingToolCall> toolCalls)
    {
        var calls = delta.GetPropertyOrDefault("tool_calls");
        if (calls.ValueKind != JsonValueKind.Array) return;
        for (var i = 0; i < calls.GetArrayLength(); i++)
        {
            var item = calls[i];
            var index = I(item, "index") ?? i;
            if (!toolCalls.TryGetValue(index, out var pending)) toolCalls[index] = pending = new PendingToolCall();
            pending.Id ??= S(item, "id");
            var function = item.GetPropertyOrDefault("function");
            pending.Name ??= S(function, "name");
            pending.Arguments += S(function, "arguments") ?? string.Empty;
        }
    }

    private static string BaseUrl(ModelDescriptor model) => string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://api.mistral.ai" : model.BaseUrl.TrimEnd('/');
    private static bool TryParse(string json, out JsonElement element) { try { using var doc = JsonDocument.Parse(json); element = doc.RootElement.Clone(); return true; } catch { element = default; return false; } }
    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? I(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.TryGetInt32(out var value) ? value : null;

    private sealed class PendingToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string Arguments { get; set; } = string.Empty;
    }
}

internal static class MistralJsonExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)) return value;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
