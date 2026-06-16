using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.OpenAI;

public sealed class OpenAIResponsesProvider : HttpModelProvider
{
    public const string ApiName = "openai-responses";

    public OpenAIResponsesProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null, string api = ApiName)
        : base(api, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        var payload = await InvokePayloadHookAsync(BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);
        using var request = CreateJsonRequest(HttpMethod.Post, OpenAIEndpoint.Url(model, "responses"), payload, credentials);
        ApplyCodexHeaders(request, model, credentials, options);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);

        var state = new TextStreamState(model);
        yield return new AssistantMessageEvent.Start(state.Message());
        if (!response.IsSuccessStatusCode)
        {
            yield return new AssistantMessageEvent.Error(state.Message("error", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)), "error");
            yield break;
        }

        var usage = new PiSharp.Abstractions.Messages.UsageInfo();
        string? responseId = null;
        string? stopReason = null;
        var toolId = string.Empty;
        var toolName = string.Empty;
        var arguments = string.Empty;

        await foreach (var evt in ReadSseAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (evt.Data == "[DONE]") break;
            if (!TryParse(evt.Data, out var root)) continue;
            var type = S(root, "type") ?? evt.Event;
            responseId ??= S(root, "response_id") ?? S(root.GetObj("response"), "id");
            switch (type)
            {
                case "response.output_text.delta":
                    foreach (var textEvt in state.AddText(S(root, "delta") ?? string.Empty)) yield return textEvt;
                    break;
                case "response.output_item.added":
                    var item = root.GetObj("item");
                    if (S(item, "type") == "function_call")
                    {
                        toolId = S(item, "call_id") ?? S(item, "id") ?? Guid.NewGuid().ToString("N");
                        toolName = S(item, "name") ?? "tool";
                        arguments = string.Empty;
                    }
                    break;
                case "response.function_call_arguments.delta":
                    arguments += S(root, "delta") ?? string.Empty;
                    break;
                case "response.output_item.done":
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        foreach (var toolEvt in state.AddToolCall(toolId, toolName, string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments)) yield return toolEvt;
                        toolId = toolName = arguments = string.Empty;
                    }
                    break;
                case "response.completed":
                    foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
                    var responseObj = root.GetObj("response");
                    stopReason = StopReasonMapper.Map(S(responseObj, "status") == "completed" ? "stop" : S(responseObj, "status"));
                    usage = ReadUsage(responseObj.GetObj("usage"));
                    yield return new AssistantMessageEvent.Done(state.Message(stopReason ?? "stop", usage: usage, responseId: responseId), stopReason ?? "stop");
                    yield break;
            }
        }

        foreach (var textEvt in state.EndTextIfOpen()) yield return textEvt;
        yield return new AssistantMessageEvent.Done(state.Message(stopReason ?? "stop", usage: usage, responseId: responseId), stopReason ?? "stop");
    }

    private static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
    {
        var tools = ToolTransformer.ToProviderTools(context.Tools);
        var isCodex = model.Api == "openai-codex-responses";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model.Id);
            if (isCodex) writer.WriteBoolean("store", false);
            writer.WriteBoolean("stream", true);
            if (isCodex) writer.WriteString("instructions", string.IsNullOrWhiteSpace(context.SystemPrompt) ? "You are a helpful assistant." : context.SystemPrompt);
            writer.WritePropertyName("input");
            WriteResponsesInput(writer, context, model.Input?.Contains("image") != false, includeSystemPrompt: !isCodex);
            if (!isCodex) writer.WriteNumber("max_output_tokens", options.MaxTokens ?? model.MaxTokens);
            if (options.Temperature is not null) writer.WriteNumber("temperature", options.Temperature.Value);
            if (isCodex)
            {
                writer.WritePropertyName("text");
                writer.WriteStartObject();
                writer.WriteString("verbosity", "low");
                writer.WriteEndObject();
                writer.WritePropertyName("include");
                JsonSerializer.Serialize(writer, new[] { "reasoning.encrypted_content" }, ProviderJson.Options);
                if (!string.IsNullOrWhiteSpace(options.SessionId)) writer.WriteString("prompt_cache_key", options.SessionId);
                writer.WriteString("tool_choice", "auto");
                writer.WriteBoolean("parallel_tool_calls", true);
            }
            ProviderToolSerializer.WriteOpenAIResponsesTools(writer, tools);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteResponsesInput(Utf8JsonWriter writer, AgentContext context, bool supportsImages, bool includeSystemPrompt)
    {
        var messages = MessageTransformer.ToProviderMessages(context, supportsImages, flattenThinking: true, includeSystemPrompt: includeSystemPrompt);
        var messageIndex = 0;
        writer.WriteStartArray();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "assistant":
                    WriteResponsesAssistantItems(writer, message.Content, ref messageIndex);
                    break;
                case "tool":
                    WriteResponsesToolOutputs(writer, message.Content);
                    break;
                default:
                    WriteResponsesMessage(writer, message.Role, message.Content, supportsImages);
                    messageIndex++;
                    break;
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteResponsesMessage(Utf8JsonWriter writer, string role, IReadOnlyList<ProviderContent> content, bool supportsImages)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        if (role == "user")
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var item in content)
            {
                if (item.Type == "text" && !string.IsNullOrEmpty(item.Text))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_text");
                    writer.WriteString("text", item.Text);
                    writer.WriteEndObject();
                    continue;
                }

                if (supportsImages && item.Type == "image" && !string.IsNullOrWhiteSpace(item.Data))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "input_image");
                    writer.WriteString("detail", "auto");
                    writer.WriteString("image_url", $"data:{item.MediaType ?? "application/octet-stream"};base64,{item.Data}");
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("content", JoinText(content));
        }
        writer.WriteEndObject();
    }

    private static void WriteResponsesAssistantItems(Utf8JsonWriter writer, IReadOnlyList<ProviderContent> content, ref int messageIndex)
    {
        var text = JoinText(content);
        if (!string.IsNullOrEmpty(text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteString("role", "assistant");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "output_text");
            writer.WriteString("text", text);
            writer.WritePropertyName("annotations");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("status", "completed");
            writer.WriteString("id", $"msg_{messageIndex++}");
            writer.WriteEndObject();
        }

        foreach (var toolCall in content.Where(item => item.Type == "tool_call" && !string.IsNullOrWhiteSpace(item.Name)))
        {
            var (callId, itemId) = SplitResponsesToolId(toolCall.Id);
            writer.WriteStartObject();
            writer.WriteString("type", "function_call");
            writer.WriteString("id", itemId ?? CreateResponsesFunctionCallItemId(callId));
            writer.WriteString("call_id", callId);
            writer.WriteString("name", toolCall.Name);
            writer.WriteString("arguments", SerializeArguments(toolCall.Arguments));
            writer.WriteEndObject();
        }
    }

    private static void WriteResponsesToolOutputs(Utf8JsonWriter writer, IReadOnlyList<ProviderContent> content)
    {
        foreach (var toolResult in content.Where(item => item.Type == "tool_result"))
        {
            var (callId, _) = SplitResponsesToolId(toolResult.ToolUseId);
            writer.WriteStartObject();
            writer.WriteString("type", "function_call_output");
            writer.WriteString("call_id", callId);
            writer.WriteString("output", toolResult.Text ?? string.Empty);
            writer.WriteEndObject();
        }
    }

    private static (string CallId, string? ItemId) SplitResponsesToolId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return ($"call_{Guid.NewGuid():N}", null);
        var parts = id.Split('|', 2);
        var callId = string.IsNullOrWhiteSpace(parts[0]) ? $"call_{Guid.NewGuid():N}" : parts[0];
        var itemId = parts.Length == 2 && parts[1].StartsWith("fc", StringComparison.Ordinal) ? parts[1] : null;
        return (callId, itemId);
    }

    private static string CreateResponsesFunctionCallItemId(string callId)
    {
        var normalized = new string(callId.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        if (normalized.StartsWith("call_", StringComparison.Ordinal)) normalized = normalized[5..];
        if (string.IsNullOrWhiteSpace(normalized)) normalized = Guid.NewGuid().ToString("N");
        var itemId = $"fc_{normalized}";
        return itemId.Length <= 64 ? itemId : itemId[..64].TrimEnd('_', '-');
    }

    private static string SerializeArguments(JsonElement? arguments)
    {
        if (arguments is null) return "{}";
        var value = arguments.Value;
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : value.GetRawText();
    }

    private static string JoinText(IEnumerable<ProviderContent> content)
        => string.Join("\n", content.Where(item => item.Type == "text" && !string.IsNullOrEmpty(item.Text)).Select(item => item.Text));

    private static void ApplyCodexHeaders(HttpRequestMessage request, ModelDescriptor model, ProviderCredentialResult credentials, AgentStreamOptions options)
    {
        if (model.Api != "openai-codex-responses") return;
        var token = ResolveBearerToken(credentials);
        if (string.IsNullOrWhiteSpace(token)) return;

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", ExtractCodexAccountId(token));
        request.Headers.TryAddWithoutValidation("originator", "pi");
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("User-Agent", RuntimeUserAgent());
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.TryAddWithoutValidation("session_id", options.SessionId);
            request.Headers.TryAddWithoutValidation("x-client-request-id", options.SessionId);
        }
    }

    private static string? ResolveBearerToken(ProviderCredentialResult credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.BearerToken)) return credentials.BearerToken;
        if (!string.IsNullOrWhiteSpace(credentials.ApiKey)) return credentials.ApiKey;
        if (credentials.Headers is not null && credentials.Headers.TryGetValue("Authorization", out var authorization))
        {
            const string prefix = "Bearer ";
            return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? authorization[prefix.Length..].Trim() : authorization;
        }
        return null;
    }

    private static string ExtractCodexAccountId(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new FormatException("Invalid JWT.");
            var payload = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("https://api.openai.com/auth", out var auth) &&
                auth.ValueKind == JsonValueKind.Object &&
                auth.TryGetProperty("chatgpt_account_id", out var accountId) &&
                accountId.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(accountId.GetString()))
            {
                return accountId.GetString()!;
            }
        }
        catch
        {
            // Normalize all parse failures to match the JavaScript provider's behavior.
        }
        throw new InvalidOperationException("Failed to extract accountId from token");
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string RuntimeUserAgent() => $"pi ({Environment.OSVersion.Platform} {Environment.OSVersion.Version}; {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    private static bool TryParse(string json, out JsonElement element) { try { using var doc = JsonDocument.Parse(json); element = doc.RootElement.Clone(); return true; } catch { element = default; return false; } }
    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static PiSharp.Abstractions.Messages.UsageInfo ReadUsage(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return new();
        var input = e.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
        var output = e.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
        return new(input, output, TotalTokens: input + output);
    }
}

internal static class OpenAIJsonExtensions
{
    public static JsonElement GetObj(this JsonElement e, string n)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)) return v;
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
