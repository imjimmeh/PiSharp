using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Extensions;
using PiSharp.Tools.Shared;

namespace PiSharp.Mcp;

/// <summary>
/// Host-level settings for the MCP client plugin, read from the <c>extensions.pisharp-mcp.*</c>
/// namespace via <see cref="IExtensionSettingsApi"/>. Keys follow plan §9:
/// enabled, autoConnect, connectTimeoutMs, reconnectMaxAttempts, reconnectDelayMs, toolPrefix,
/// maxToolResultLines, maxToolResultBytes, mcpServers.
/// </summary>
public sealed record McpHostOptions(
    bool Enabled = true,
    bool AutoConnect = true,
    TimeSpan ConnectTimeout = default,
    int ReconnectMaxAttempts = 5,
    TimeSpan ReconnectDelay = default,
    string ToolPrefix = "mcp",
    int? MaxToolResultLines = null,
    int? MaxToolResultBytes = null,
    IReadOnlyDictionary<string, McpServerConfig>? Servers = null)
{
    private static readonly Regex ToolPrefixPattern = new("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant);

    public TruncationOptions? Truncation => MaxToolResultLines is null && MaxToolResultBytes is null
        ? null
        : new TruncationOptions(MaxToolResultLines, MaxToolResultBytes);

    public static McpHostOptions Default { get; } = new();

    public static McpHostOptions FromSettings(IExtensionSettingsApi settings)
    {
        var connectTimeoutMs = ReadInt(settings, "connectTimeoutMs") ?? 10000;
        var reconnectDelayMs = ReadInt(settings, "reconnectDelayMs") ?? 2000;
        var toolPrefix = ReadString(settings, "toolPrefix") ?? "mcp";

        return new McpHostOptions(
            Enabled: ReadBool(settings, "enabled") ?? true,
            AutoConnect: ReadBool(settings, "autoConnect") ?? true,
            ConnectTimeout: TimeSpan.FromMilliseconds(Math.Max(1, connectTimeoutMs)),
            ReconnectMaxAttempts: Math.Max(0, ReadInt(settings, "reconnectMaxAttempts") ?? 5),
            ReconnectDelay: TimeSpan.FromMilliseconds(Math.Max(1, reconnectDelayMs)),
            ToolPrefix: ToolPrefixPattern.IsMatch(toolPrefix) ? toolPrefix : "mcp",
            MaxToolResultLines: ReadInt(settings, "maxToolResultLines"),
            MaxToolResultBytes: ReadInt(settings, "maxToolResultBytes"),
            Servers: ReadServers(settings));
    }

    public static IReadOnlyDictionary<string, McpServerConfig> ReadServers(IExtensionSettingsApi settings)
    {
        var element = ReadElement(settings, "mcpServers");
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var property in element.Value.EnumerateObject())
        {
            var name = McpServerConfig.NormalizeName(property.Name);
            if (name.Length == 0 || property.Value.ValueKind != JsonValueKind.Object) continue;
            var config = ParseServer(name, property.Value);
            if (config is not null) servers[name] = config;
        }
        return servers;
    }

    private static McpServerConfig? ParseServer(string name, JsonElement server)
    {
        var url = ReadString(server, "url");
        var transport = string.IsNullOrWhiteSpace(url) ? McpTransportKind.Stdio : McpTransportKind.Http;
        var auth = ReadAuth(server);

        var config = new McpServerConfig(
            Name: name,
            Source: "settings",
            Transport: transport,
            Command: ReadString(server, "command"),
            Args: ReadStringList(server, "args"),
            Env: ReadStringMap(server, "env"),
            Cwd: ReadString(server, "cwd"),
            Url: url,
            HttpMode: ReadString(server, "httpMode") ?? "streamable-http",
            Headers: ReadStringMap(server, "headers"),
            Auth: auth,
            Enabled: ReadBool(server, "enabled") ?? true);

        // Invalid servers become per-server error states; the host reports them instead of failing.
        return config;
    }

    private static McpAuthConfig? ReadAuth(JsonElement server)
    {
        var kind = McpAuthKind.None;
        var kindText = ReadString(server, "auth.type");
        if (string.IsNullOrWhiteSpace(kindText) && server.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
            kindText = ReadString(auth, "type");
        kind = kindText?.ToLowerInvariant() switch
        {
            "env" => McpAuthKind.Env,
            "literal" => McpAuthKind.Literal,
            "oauth" => McpAuthKind.OAuth,
            _ => McpAuthKind.None
        };

        return new McpAuthConfig(
            kind,
            EnvVar: FirstString(server, "auth.envVar", "envVar"),
            LiteralToken: FirstString(server, "auth.token", "token"),
            ClientId: FirstString(server, "auth.clientId", "clientId"));
    }

    private static string? FirstString(JsonElement server, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadString(server, key);
            if (value is not null) return value;
        }
        return null;
    }

    private static bool? ReadBool(JsonElement server, string key)
        => server.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? ReadInt(IExtensionSettingsApi settings, string key)
    {
        var element = ReadElement(settings, key);
        if (element is null) return null;
        return element.Value.ValueKind == JsonValueKind.Number && element.Value.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? ReadString(IExtensionSettingsApi settings, string key)
    {
        var element = ReadElement(settings, key);
        return element is null || element.Value.ValueKind != JsonValueKind.String
            ? null
            : element.Value.GetString();
    }

    private static bool? ReadBool(IExtensionSettingsApi settings, string key)
    {
        var element = ReadElement(settings, key);
        return element is null ? null
            : element.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.Value.GetBoolean() : null;
    }

    private static JsonElement? ReadElement(IExtensionSettingsApi settings, string key)
    {
        var raw = settings.Get(key);
        if (raw is null) return null;
        var node = ExtensionSettingKeys.ToJsonNode(raw);
        if (node is null) return null;
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    private static string? ReadString(JsonElement server, string key)
        => server.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string>? ReadStringList(JsonElement server, string key)
    {
        if (!server.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) items.Add(item.GetString()!);
        return items;
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonElement server, string key)
    {
        if (!server.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String) map[property.Name] = property.Value.GetString()!;
        return map;
    }
}
