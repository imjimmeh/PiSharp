namespace PiSharp.Agent.Core.Models;

public sealed record ModelDescriptor(
    string Provider,
    string Id,
    string Api,
    string Name = "",
    string BaseUrl = "",
    bool Reasoning = false,
    int ContextWindow = 0,
    int MaxTokens = 0,
    IReadOnlyDictionary<string, int>? ThinkingLevelMap = null,
    IReadOnlyList<string>? Input = null,
    ModelCost? Cost = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    ModelCompat? Compat = null,
    string? ApiKey = null,
    bool? AuthHeader = null);

public sealed record ModelCost(
    decimal Input = 0,
    decimal Output = 0,
    decimal CacheRead = 0,
    decimal CacheWrite = 0);

public abstract record ModelCompat;

public sealed record AnthropicCompat(
    string? CacheControl = null) : ModelCompat;

public sealed record OpenAICompat(
    bool Strict = false,
    string? MaxTokensField = null) : ModelCompat;
