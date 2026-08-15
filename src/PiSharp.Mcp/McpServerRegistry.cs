using System.Collections.Concurrent;

namespace PiSharp.Mcp;

/// <summary>
/// Static registries for transport factories and extension-contributed servers. Mirrors
/// <c>OAuthProviderRegistry</c>'s ConcurrentDictionary pattern. Transport plugins register their
/// factories during <c>InitializeAsync</c>; any extension may contribute ephemeral servers that
/// the host reconciles alongside the settings map.
/// </summary>
public static class McpServerRegistry
{
    private static readonly ConcurrentDictionary<string, IMcpTransportFactory> Factories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, McpServerConfig> ContributedServers = new(StringComparer.Ordinal);

    /// <summary>Registers a transport factory; throws on duplicate kind.</summary>
    public static void RegisterFactory(IMcpTransportFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!Factories.TryAdd(factory.Kind, factory))
            throw new InvalidOperationException($"An MCP transport factory for kind '{factory.Kind}' is already registered.");
    }

    public static IMcpTransportFactory? FindFactory(McpTransportKind kind)
        => FindFactory(KindName(kind));

    public static IMcpTransportFactory? FindFactory(string kind)
    {
        Factories.TryGetValue(kind, out var factory);
        return factory;
    }

    public static IReadOnlyList<IMcpTransportFactory> GetFactories()
        => Factories.Values.ToArray();

    /// <summary>
    /// Registers an extension-contributed server (ephemeral; settings wins on conflict). The
    /// server keeps the provenance <see cref="McpServerConfig.SourceId"/> = <paramref name="sourceId"/>
    /// on the stored config so the host can honor the originating extension in spawn-gate
    /// decisions.
    /// </summary>
    public static void RegisterServer(string sourceId, McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
        ContributedServers[sourceId] = config with { SourceId = sourceId };
    }

    public static void UnregisterBySource(string sourceId)
        => ContributedServers.TryRemove(sourceId, out _);

    public static IReadOnlyList<McpServerConfig> GetContributedServers()
        => ContributedServers.Values.ToArray();

    public static void ClearForTesting()
    {
        Factories.Clear();
        ContributedServers.Clear();
    }

    internal static string KindName(McpTransportKind kind)
        => kind switch
        {
            McpTransportKind.Stdio => "stdio",
            McpTransportKind.Http => "http",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
