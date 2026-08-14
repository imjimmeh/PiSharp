using System.Text.Json.Nodes;
using PiSharp.Compatibility.Resources;

namespace PiSharp.Packages;

public sealed class NpmOutdatedChecker(INpmRegistryClient registryClient)
{
    public async Task<IReadOnlyList<OutdatedPackageInfo>> CheckAsync(
        IReadOnlyList<PiResolvedPackage> packages,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = new List<OutdatedPackageInfo>();
            foreach (var package in packages)
            {
                if (!string.Equals(package.Source, "npm", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = PiPackageSourceParser.Parse(package.Reference);
                if (parsed.IsPinned) continue;

                var installedVersion = ReadJsonField(package.RootPath, "version");
                if (installedVersion is null) continue;

                var packageName = ReadJsonField(package.RootPath, "name") ?? parsed.Name;
                var latestVersion = await registryClient.GetLatestVersionAsync(packageName, cancellationToken);
                if (latestVersion is null) continue;

                if (NpmVersionComparer.IsOlderThan(installedVersion, latestVersion))
                    results.Add(new OutdatedPackageInfo(packageName, installedVersion, latestVersion));
            }
            return results;
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadJsonField(string rootPath, string field)
    {
        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
            return json?[field]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
