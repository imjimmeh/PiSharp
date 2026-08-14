namespace PiSharp.Packages;

public interface INpmRegistryClient
{
    Task<string?> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken = default);
}
