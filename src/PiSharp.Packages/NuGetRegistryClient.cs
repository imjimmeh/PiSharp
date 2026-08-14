using System.Text.Json.Nodes;

namespace PiSharp.Packages;

/// <summary>NuGet v3 flat-container client: highest stable version for a package id.</summary>
public sealed class NuGetRegistryClient(HttpClient httpClient) : INuGetRegistryClient
{
    public async Task<string?> GetLatestStableVersionAsync(string packageId, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = packageId.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3-flatcontainer/{Uri.EscapeDataString(id)}/index.json";
            var response = await httpClient.GetStringAsync(url, cancellationToken);
            var json = JsonNode.Parse(response) as JsonObject;
            var versions = json?["versions"] as JsonArray;
            if (versions is null) return null;

            string? best = null;
            foreach (var item in versions)
            {
                var version = item?.GetValue<string>();
                if (version is null) continue;
                if (IsStable(version) && (best is null || NuGetVersionComparer.IsOlderThan(best, version)))
                    best = version;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsStable(string version)
        => NuGetVersionComparer.ParseStability(version) == VersionStability.Stable;
}
