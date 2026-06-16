namespace PiSharp.Cli.Packages;

public interface INpmRegistryClient
{
    Task<string?> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken = default);
}
