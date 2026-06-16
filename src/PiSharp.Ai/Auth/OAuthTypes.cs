namespace PiSharp.Ai.Auth;

public sealed record OAuthCredentials(
    string Refresh,
    string Access,
    long Expires,
    IReadOnlyDictionary<string, object?>? Extra = null);

public sealed record OAuthAuthInfo(string Url, string? Instructions = null);

public sealed record OAuthPrompt(string Message, string? Placeholder = null, bool AllowEmpty = false);

public sealed record OAuthSelectOption(string Id, string Label);

public sealed record OAuthSelectPrompt(string Message, IReadOnlyList<OAuthSelectOption> Options);
