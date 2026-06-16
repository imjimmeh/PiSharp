namespace PiSharp.Ai.Auth;

public sealed record OAuthLoginCallbacks(
    Func<OAuthAuthInfo, Task> OnAuth,
    Func<OAuthPrompt, CancellationToken, Task<string>> OnPrompt,
    Func<string, Task>? OnProgress = null,
    Func<CancellationToken, Task<string>>? OnManualCodeInput = null);

public interface IOAuthProvider
{
    string Id { get; }
    string Name { get; }
    bool UsesCallbackServer { get; }
    Task<OAuthCredentials> LoginAsync(OAuthLoginCallbacks callbacks, CancellationToken cancellationToken = default);
    Task<OAuthCredentials> RefreshTokenAsync(OAuthCredentials credentials, CancellationToken cancellationToken = default);
    string GetApiKey(OAuthCredentials credentials);
}
