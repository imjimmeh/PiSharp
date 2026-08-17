using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Http;

namespace PiSharp.Ai.Providers.Shared;

public abstract class HttpModelProvider : IModelProvider
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(10);

    protected readonly HttpClient HttpClient;
    protected readonly IProviderCredentialResolver CredentialResolver;
    protected readonly ILogger Logger;

    protected HttpModelProvider(string api, HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null, ILoggerFactory? loggerFactory = null)
    {
        Api = api;
        HttpClient = httpClient ?? new HttpClient { Timeout = DefaultRequestTimeout };
        CredentialResolver = credentialResolver ?? new ProviderCredentialResolver();
        Logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    public string Api { get; }

    public abstract IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);

    public async Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
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

        return terminal ?? NewMessage(model, []);
    }

    protected async Task<ProviderCredentialResult> ResolveCredentialsAsync(
        ModelDescriptor model,
        AgentStreamOptions options,
        bool requireAuthentication = true,
        CancellationToken cancellationToken = default)
    {
        var credentials = await CredentialResolver.ResolveAsync(model, options, cancellationToken).ConfigureAwait(false);
        if (requireAuthentication && !credentials.IsAuthenticated)
        {
            Logger.LogWarning("No credentials available for provider {Provider}", model.Provider);
            throw new InvalidOperationException($"No credentials available for provider '{model.Provider}'.");
        }

        return credentials;
    }

    protected static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, JsonElement payload, ProviderCredentialResult credentials)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json")
        };
        ApplyHeaders(request, credentials);
        return request;
    }

    protected static void ApplyHeaders(HttpRequestMessage request, ProviderCredentialResult credentials)
    {
        if (credentials.Headers is not null)
        {
            foreach (var header in credentials.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(credentials.BearerToken) && !request.Headers.Contains("Authorization"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.BearerToken);
        }
    }

    protected static async Task<JsonElement> InvokePayloadHookAsync(JsonElement payload, AgentStreamOptions options, CancellationToken cancellationToken)
        => options.OnPayload is null ? payload : await options.OnPayload(payload, cancellationToken).ConfigureAwait(false);

    protected static async Task InvokeResponseHookAsync(HttpResponseMessage response, AgentStreamOptions options, CancellationToken cancellationToken)
    {
        if (options.OnResponse is null) return;
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        await options.OnResponse(new ProviderResponseInfo((int)response.StatusCode, headers), cancellationToken).ConfigureAwait(false);
    }

    internal static AssistantMessage NewMessage(
        ModelDescriptor model,
        IReadOnlyList<MessageContent> content,
        string? stopReason = null,
        string? error = null,
        UsageInfo? usage = null,
        string? responseId = null)
        => new(content.ToArray(), Api: model.Api, Provider: model.Provider, Model: model.Id, Usage: usage, StopReason: stopReason, ErrorMessage: error, ResponseId: responseId);

    protected static async IAsyncEnumerable<AssistantMessageEvent> ErrorAfterStart(
        ModelDescriptor model,
        string error,
        ILogger? logger = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger?.LogWarning("Provider error after start for {Model} ({Provider}): {Error}", model.Id, model.Provider, error);
        var start = NewMessage(model, []);
        yield return new AssistantMessageEvent.Start(start);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var terminal = NewMessage(model, [], stopReason: "error", error: error);
        yield return new AssistantMessageEvent.Error(terminal, "error");
    }

    internal static async IAsyncEnumerable<SseEvent> ReadSseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var evt in SseParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    internal static JsonElement Json(params (string Key, object? Value)[] properties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in properties)
            {
                if (value is null) continue;
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value);
            }
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}

internal sealed class TextStreamState
{
    private readonly ModelDescriptor _model;
    private readonly List<MessageContent> _content = [];
    private int? _textIndex;
    private string _text = string.Empty;
    private int? _thinkingIndex;
    private string _thinking = string.Empty;

    public TextStreamState(ModelDescriptor model) => _model = model;

    public AssistantMessage Message(string? stopReason = null, string? error = null, UsageInfo? usage = null, string? responseId = null)
        => HttpModelProvider.NewMessage(_model, _content, stopReason, error, usage, responseId);

    public IEnumerable<AssistantMessageEvent> AddText(string delta)
    {
        if (string.IsNullOrEmpty(delta)) yield break;
        if (_textIndex is null)
        {
            _textIndex = _content.Count;
            _content.Add(new TextContent(string.Empty));
            yield return new AssistantMessageEvent.TextStart(Message(), _textIndex.Value);
        }

        _text += delta;
        _content[_textIndex.Value] = new TextContent(_text);
        yield return new AssistantMessageEvent.TextDelta(Message(), _textIndex.Value, delta);
    }

    public IEnumerable<AssistantMessageEvent> EndTextIfOpen()
    {
        if (_textIndex is null) yield break;
        yield return new AssistantMessageEvent.TextEnd(Message(), _textIndex.Value);
        _textIndex = null;
        _text = string.Empty;
    }

    public IEnumerable<AssistantMessageEvent> AddThinking(string delta)
    {
        if (string.IsNullOrEmpty(delta)) yield break;
        if (_thinkingIndex is null)
        {
            _thinkingIndex = _content.Count;
            _content.Add(new ThinkingContent(string.Empty));
            yield return new AssistantMessageEvent.ThinkingStart(Message(), _thinkingIndex.Value);
        }

        _thinking += delta;
        _content[_thinkingIndex.Value] = new ThinkingContent(_thinking);
        yield return new AssistantMessageEvent.ThinkingDelta(Message(), _thinkingIndex.Value, delta);
    }

    public IEnumerable<AssistantMessageEvent> EndThinkingIfOpen()
    {
        if (_thinkingIndex is null) yield break;
        yield return new AssistantMessageEvent.ThinkingEnd(Message(), _thinkingIndex.Value);
        _thinkingIndex = null;
        _thinking = string.Empty;
    }

    public IEnumerable<AssistantMessageEvent> AddToolCall(string id, string name, string arguments)
    {
        var index = _content.Count;
        yield return new AssistantMessageEvent.ToolCallStart(Message(), index);
        yield return new AssistantMessageEvent.ToolCallDelta(Message(), index, arguments);
        var call = new ToolCallContent(ToolTransformer.NormalizeToolCallId(id), name, ToolTransformer.ParseArgumentsOrEmpty(arguments));
        _content.Add(call);
        yield return new AssistantMessageEvent.ToolCallEnd(Message(), index, call);
    }
}
