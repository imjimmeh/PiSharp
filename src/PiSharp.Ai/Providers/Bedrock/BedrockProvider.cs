using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Bedrock;

public sealed class BedrockProvider : HttpModelProvider
{
    public const string ApiName = "bedrock-converse-stream";

    public BedrockProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        : base(ApiName, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        using var request = CreateJsonRequest(HttpMethod.Post, new Uri($"{BaseUrl(model)}/model/{Uri.EscapeDataString(model.Id)}/converse-stream"), payload, credentials);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);

        var state = new TextStreamState(model);
        yield return new AssistantMessageEvent.Start(state.Message());
        if (!response.IsSuccessStatusCode)
        {
            yield return new AssistantMessageEvent.Error(state.Message("error", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)), "error");
            yield break;
        }

        var toolId = string.Empty;
        var toolName = string.Empty;
        var toolInput = string.Empty;
        var isReasoningBlock = false;
        await foreach (var evt in ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (!TryParse(evt.Data, out var root)) continue;

            if (Obj(Obj(root, "contentBlockStart"), "start").TryGetProperty("reasoningContent", out _))
            {
                isReasoningBlock = true;
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contentBlockStop", out _))
            {
                foreach (var thinkEvt in state.EndThinkingIfOpen()) yield return thinkEvt;
                isReasoningBlock = false;
            }

            var deltaText = root.GetPathString("contentBlockDelta", "delta", "text");

            if (isReasoningBlock)
            {
                foreach (var thinkEvt in state.AddThinking(deltaText ?? string.Empty)) yield return thinkEvt;
            }
            else
            {
                var text = deltaText ?? root.GetPathString("output", "message", "content", 0, "text") ?? root.GetPathString("text");
                foreach (var textEvt in state.AddText(text ?? string.Empty)) yield return textEvt;
            }

            var startTool = Obj(Obj(Obj(root, "contentBlockStart"), "start"), "toolUse");
            toolId = S(startTool, "toolUseId") ?? toolId;
            toolName = S(startTool, "name") ?? toolName;
            var inputDelta = root.GetPathString("contentBlockDelta", "delta", "toolUse", "input");
            toolInput += inputDelta ?? string.Empty;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contentBlockStop", out _) && !string.IsNullOrWhiteSpace(toolName))
            {
                foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                foreach (var toolEvt in state.AddToolCall(string.IsNullOrWhiteSpace(toolId) ? Guid.NewGuid().ToString("N") : toolId, toolName, string.IsNullOrWhiteSpace(toolInput) ? "{}" : toolInput)) yield return toolEvt;
                toolId = toolName = toolInput = string.Empty;
            }

            var outputTool = Obj(Obj(Obj(root, "output"), "message"), "content");
            if (outputTool.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputTool.EnumerateArray())
                {
                    var toolUse = Obj(item, "toolUse");
                    var name = S(toolUse, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var args = toolUse.ValueKind == JsonValueKind.Object && toolUse.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
                    foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                    foreach (var toolEvt in state.AddToolCall(S(toolUse, "toolUseId") ?? Guid.NewGuid().ToString("N"), name, args)) yield return toolEvt;
                }
            }
        }

        foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
        foreach (var thinkEvt in state.EndThinkingIfOpen()) yield return thinkEvt;
        yield return new AssistantMessageEvent.Done(state.Message("stop"), "stop");
    }

    private static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
    {
        var tools = ToolTransformer.ToProviderTools(context.Tools);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, MessageTransformer.ToProviderMessages(context, model.Input?.Contains("image") != false, flattenThinking: true), ProviderJson.Options);
            if (options.MaxTokens is not null)
            {
                writer.WritePropertyName("inferenceConfig");
                writer.WriteStartObject();
                writer.WriteNumber("maxTokens", options.MaxTokens.Value);
                if (options.Temperature is not null) writer.WriteNumber("temperature", options.Temperature.Value);
                writer.WriteEndObject();
            }
            ProviderToolSerializer.WriteBedrockToolConfig(writer, tools);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static string BaseUrl(ModelDescriptor model) => string.IsNullOrWhiteSpace(model.BaseUrl) ? "https://bedrock-runtime.us-east-1.amazonaws.com" : model.BaseUrl.TrimEnd('/');
    private static bool TryParse(string json, out JsonElement element) { try { using var doc = JsonDocument.Parse(json); element = doc.RootElement.Clone(); return true; } catch { element = default; return false; } }
    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Obj(JsonElement e, string n)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)) return v;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}

internal static class BedrockJsonExtensions
{
    public static string? GetPathString(this JsonElement e, params object[] path)
    {
        var current = e;
        foreach (var segment in path)
        {
            if (segment is string name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
            }
            else if (segment is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index) return null;
                current = current[index];
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
