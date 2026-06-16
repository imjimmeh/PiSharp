namespace PiSharp.Ai.Models;

public sealed record ModelProviderConfig(
    string Provider,
    string? Api = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool? AuthHeader = null);
