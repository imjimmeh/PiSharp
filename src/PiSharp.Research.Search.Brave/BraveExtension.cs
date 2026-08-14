using PiSharp.Ai.Auth;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-research-search-brave",
    Name = "Brave search provider",
    Version = "1.0.0",
    Description = "Registers the 'brave' web search provider (Brave Search Web Search API). Requires BRAVE_API_KEY.")]

namespace PiSharp.Research.Search.Brave;

/// <summary>
/// <c>pisharp-research-search-brave</c> extension entry: registers the
/// <see cref="BraveSearchProvider"/> and extends the ambient credential map
/// with <c>BRAVE_API_KEY</c>. Optional — the research plugin works without it.
/// </summary>
public sealed class BraveExtension : IExtension, IDisposable
{
    private BraveSearchProvider? _provider;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("brave", ["BRAVE_API_KEY"]);
        _provider = new BraveSearchProvider();
        api.Search.RegisterProvider(_provider);
        return Task.CompletedTask;
    }

    public void Dispose() => _provider?.Dispose();
}
