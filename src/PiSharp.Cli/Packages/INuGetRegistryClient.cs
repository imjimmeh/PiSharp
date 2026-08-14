namespace PiSharp.Cli.Packages;

public interface INuGetRegistryClient
{
    /// <summary>Highest stable version of <paramref name="packageId"/> on the default feed,
    /// or null when unreachable/unparseable (never throws). Package id is lowercased.</summary>
    Task<string?> GetLatestStableVersionAsync(string packageId, CancellationToken cancellationToken = default);
}
