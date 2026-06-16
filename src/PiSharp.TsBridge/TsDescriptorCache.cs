using System.Security.Cryptography;
using System.Text;
using PiSharp.Agent.Serialization;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge;

internal sealed class TsDescriptorCache(bool enabled, string? cacheDirectory)
{
    public async Task PersistAsync(TsExtensionDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!enabled || string.IsNullOrWhiteSpace(cacheDirectory)) return;
        try
        {
            var path = DescriptorCachePath(descriptor.ExtensionPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, AgentJsonSerializer.Serialize(descriptor), cancellationToken);
        }
        catch
        {
            // Descriptor cache is an optimization; extension activation has already succeeded.
        }
    }

    public async Task<TsExtensionDescriptor?> ReadAsync(string extensionPath, CancellationToken cancellationToken)
    {
        if (!enabled || string.IsNullOrWhiteSpace(cacheDirectory)) return null;
        var path = DescriptorCachePath(extensionPath);
        var sourcePath = ResolveDescriptorSourcePath(extensionPath);
        if (!File.Exists(path) || sourcePath is null) return null;
        var descriptor = AgentJsonSerializer.Deserialize<TsExtensionDescriptor>(await File.ReadAllTextAsync(path, cancellationToken));
        if (descriptor is null || descriptor.SchemaVersion != 1) return null;
        if (!StringComparer.Ordinal.Equals(Path.GetFullPath(descriptor.ExtensionPath), Path.GetFullPath(extensionPath))) return null;
        if (!string.IsNullOrWhiteSpace(descriptor.PackageName) && !string.IsNullOrWhiteSpace(descriptor.PackageVersion))
        {
            var currentPackage = await ResolveInstalledPackageMetadataAsync(sourcePath, cancellationToken);
            if (currentPackage is null) return null;
            return string.Equals(descriptor.PackageName, currentPackage.Value.Name, StringComparison.Ordinal)
                && string.Equals(descriptor.PackageVersion, currentPackage.Value.Version, StringComparison.Ordinal)
                ? descriptor
                : null;
        }

        var currentHash = await HashFileAsync(sourcePath, cancellationToken);
        if (!string.Equals(descriptor.SourceHash, currentHash, StringComparison.Ordinal)) return null;
        foreach (var dependency in descriptor.DependencyHashes ?? [])
        {
            if (!File.Exists(dependency.Path)) return null;
            var dependencyHash = await HashFileAsync(dependency.Path, cancellationToken);
            if (!string.Equals(dependency.Hash, dependencyHash, StringComparison.Ordinal)) return null;
        }
        return descriptor;
    }

    private static async Task<(string Name, string Version)?> ResolveInstalledPackageMetadataAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var segments = fullPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (!segments.Any(segment => string.Equals(segment, "node_modules", StringComparison.Ordinal))) return null;

        var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var packageJsonPath = Path.Combine(directory, "package.json");
            if (File.Exists(packageJsonPath))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(packageJsonPath, cancellationToken));
                    var root = document.RootElement;
                    var hasName = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == System.Text.Json.JsonValueKind.String;
                    var hasVersion = root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == System.Text.Json.JsonValueKind.String;
                    if (hasName && hasVersion && nameElement.GetString() is { Length: > 0 } name && versionElement.GetString() is { Length: > 0 } version)
                    {
                        return (name, version);
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    private string DescriptorCachePath(string extensionPath)
        => Path.Combine(cacheDirectory!, "descriptors", $"{HashText(Path.GetFullPath(extensionPath))}.json");

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
        => HashText(await File.ReadAllTextAsync(path, cancellationToken));

    private static string? ResolveDescriptorSourcePath(string extensionPath)
    {
        var fullPath = Path.GetFullPath(extensionPath);
        if (File.Exists(fullPath)) return fullPath;
        if (!Directory.Exists(fullPath)) return null;

        foreach (var indexName in new[] { "index.ts", "index.mjs", "index.js" })
        {
            var candidate = Path.Combine(fullPath, indexName);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var child in Directory.EnumerateDirectories(fullPath).OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var indexName in new[] { "index.ts", "index.mjs", "index.js" })
            {
                var candidate = Path.Combine(child, indexName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
