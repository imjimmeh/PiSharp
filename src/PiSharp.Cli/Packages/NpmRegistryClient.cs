using System.Text.Json.Nodes;

namespace PiSharp.Cli.Packages;

public sealed class NpmRegistryClient(HttpClient httpClient) : INpmRegistryClient
{
    public async Task<string?> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://registry.npmjs.org/{Uri.EscapeDataString(packageName)}/latest";
            var response = await httpClient.GetStringAsync(url, cancellationToken);
            var json = JsonNode.Parse(response) as JsonObject;
            return json?["version"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
