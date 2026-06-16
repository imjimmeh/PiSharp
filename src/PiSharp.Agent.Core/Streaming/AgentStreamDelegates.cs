using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Models;

namespace PiSharp.Agent.Core.Streaming;

public sealed record AgentStreamOptions(
    string? ApiKey = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? Reasoning = null,
    string? SessionId = null,
    string? Transport = null,
    int? TimeoutMs = null,
    int? MaxRetries = null,
    int? MaxRetryDelayMs = null,
    string? CacheRetention = null,
    decimal? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyDictionary<string, object?>? ProviderExtensions = null,
    Func<JsonElement, CancellationToken, Task<JsonElement>>? OnPayload = null,
    Func<ProviderResponseInfo, CancellationToken, Task>? OnResponse = null);

public sealed record ProviderResponseInfo(
    int Status,
    IReadOnlyDictionary<string, string> Headers);

public delegate IAsyncEnumerable<AssistantMessageEvent> AgentStreamAsync(
    ModelDescriptor model,
    AgentContext context,
    AgentStreamOptions options,
    CancellationToken cancellationToken = default);

public delegate Task<AssistantMessage> AgentCompletionAsync(
    ModelDescriptor model,
    AgentContext context,
    AgentStreamOptions options,
    CancellationToken cancellationToken = default);
