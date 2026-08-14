using PiSharp.Ai.Auth;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-research-search-google-cse",
    Name = "Google Custom Search provider",
    Version = "1.0.0",
    Description = "Registers the 'google-cse' web search provider (Google Custom Search JSON API). Requires GOOGLE_CSE_API_KEY and a configured cx.")]

namespace PiSharp.Research.Search.GoogleCse;

/// <summary>
/// <c>pisharp-research-search-google-cse</c> extension entry: registers the
/// <see cref="GoogleCseSearchProvider"/> and extends the ambient credential map
/// with <c>GOOGLE_CSE_API_KEY</c>. Optional — the research plugin works without it.
/// </summary>
public sealed class GoogleCseExtension : IExtension, IDisposable
{
    private GoogleCseSearchProvider? _provider;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("google-cse", ["GOOGLE_CSE_API_KEY"]);
        _provider = new GoogleCseSearchProvider();
        api.Search.RegisterProvider(_provider);
        return Task.CompletedTask;
    }

    public void Dispose() => _provider?.Dispose();
}
