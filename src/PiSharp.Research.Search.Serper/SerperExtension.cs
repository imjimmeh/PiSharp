using PiSharp.Ai.Auth;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-research-search-serper",
    Name = "Serper search provider",
    Version = "1.0.0",
    Description = "Registers the 'serper' web search provider (Serper Google SERP API). Requires SERPER_API_KEY.")]

namespace PiSharp.Research.Search.Serper;

/// <summary>
/// <c>pisharp-research-search-serper</c> extension entry: registers the
/// <see cref="SerperSearchProvider"/> and extends the ambient credential map
/// with <c>SERPER_API_KEY</c>. Optional — the research plugin works without it.
/// </summary>
public sealed class SerperExtension : IExtension, IDisposable
{
    private SerperSearchProvider? _provider;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("serper", ["SERPER_API_KEY"]);
        _provider = new SerperSearchProvider();
        api.Search.RegisterProvider(_provider);
        return Task.CompletedTask;
    }

    public void Dispose() => _provider?.Dispose();
}
