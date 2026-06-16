using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Ai.Auth;

public sealed class FileOAuthStorage(string path) : IOAuthStorage
{
    public string Path { get; } = path;

    public async Task<string?> GetTokenAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || !File.Exists(Path)) return null;
        await using var stream = File.OpenRead(Path);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }, cancellationToken).ConfigureAwait(false);
        return FindProviderToken(document.RootElement, provider);
    }

    public async Task SetTokenAsync(string provider, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        var root = await LoadMutableAsync(cancellationToken).ConfigureAwait(false);
        var providers = EnsureObject(root, "providers");
        var entry = providers[provider] as JsonObject ?? [];
        entry["token"] = token;
        providers[provider] = entry;
        await SaveAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveTokenAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || !File.Exists(Path)) return;
        var root = await LoadMutableAsync(cancellationToken).ConfigureAwait(false);
        if (root["providers"] is JsonObject providers) providers.Remove(provider);
        root.Remove(provider);
        await SaveAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetOAuthCredentialsAsync(string provider, OAuthCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        var root = await LoadMutableAsync(cancellationToken).ConfigureAwait(false);
        root.Remove(provider);
        var providers = EnsureObject(root, "providers");
        var entry = providers[provider] as JsonObject ?? [];
        entry["access"] = credentials.Access;
        entry["refresh"] = credentials.Refresh;
        entry["expires"] = credentials.Expires;
        if (credentials.Extra is { Count: > 0 })
        {
            foreach (var (key, value) in credentials.Extra)
            {
                entry[key] = value switch
                {
                    null => null,
                    string s => JsonValue.Create(s),
                    long l => JsonValue.Create(l),
                    int i => JsonValue.Create(i),
                    bool b => JsonValue.Create(b),
                    _ => JsonValue.Create(value.ToString())
                };
            }
        }
        providers[provider] = entry;
        await SaveAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OAuthCredentials?> GetOAuthCredentialsAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || !File.Exists(Path)) return null;
        await using var stream = File.OpenRead(Path);
        using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }, cancellationToken).ConfigureAwait(false);

        if (!TryGetProviderEntryAnywhere(document.RootElement, provider, out var entry)) return null;
        if (entry.ValueKind != JsonValueKind.Object) return null;

        string? access = null, refresh = null;
        long expires = 0;
        var hasAccess = false;

        foreach (var prop in entry.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "access" when prop.Value.ValueKind == JsonValueKind.String:
                    access = prop.Value.GetString();
                    hasAccess = true;
                    break;
                case "refresh" when prop.Value.ValueKind == JsonValueKind.String:
                    refresh = prop.Value.GetString();
                    break;
                case "expires" when prop.Value.ValueKind == JsonValueKind.Number:
                    expires = prop.Value.GetInt64();
                    break;
            }
        }

        if (!hasAccess) return null;

        var extra = new Dictionary<string, object?>();
        foreach (var prop in entry.EnumerateObject())
        {
            if (prop.Name is "access" or "refresh" or "expires" or "token") continue;
            extra[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.GetRawText()
            };
        }

        return new OAuthCredentials(refresh ?? "", access ?? "", expires, extra.Count > 0 ? extra : null);
    }

    public async Task<IReadOnlyList<string>> ListStoredProvidersAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return [];
        var json = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return [];

        var providers = new List<string>();
        if (root["providers"] is JsonObject providerNode)
        {
            providers.AddRange(providerNode.Select(p => p.Key));
        }
        foreach (var (key, _) in root.Where(kv => kv.Key is not "providers"))
        {
            providers.Add(key);
        }
        return providers;
    }

    private static bool TryGetProviderEntryAnywhere(JsonElement root, string provider, out JsonElement entry)
    {
        entry = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("providers", out var providers))
        {
            foreach (var property in providers.EnumerateObject())
            {
                if (!string.Equals(property.Name, provider, StringComparison.OrdinalIgnoreCase)) continue;
                entry = property.Value;
                return true;
            }
        }
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, provider, StringComparison.OrdinalIgnoreCase)) continue;
            entry = property.Value;
            return true;
        }
        return false;
    }

    private static string? FindProviderToken(JsonElement root, string provider)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("providers", out var providers) && TryGetProviderEntry(providers, provider, out var nested)) return TokenFromEntry(nested);
        if (TryGetProviderEntry(root, provider, out var entry)) return TokenFromEntry(entry);
        return null;
    }

    private static bool TryGetProviderEntry(JsonElement root, string provider, out JsonElement entry)
    {
        entry = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, provider, StringComparison.OrdinalIgnoreCase)) continue;
            entry = property.Value;
            return true;
        }
        return false;
    }

    private static string? TokenFromEntry(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String) return NonEmpty(entry.GetString());
        if (entry.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "token", "access", "accessToken", "access_token", "apiKey", "api_key", "key" })
        {
            if (entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && NonEmpty(value.GetString()) is { } token) return token;
        }
        foreach (var childName in new[] { "oauth", "oauthToken", "credentials", "auth" })
        {
            if (entry.TryGetProperty(childName, out var child) && TokenFromEntry(child) is { } token) return token;
        }
        return null;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<JsonObject> LoadMutableAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path)) return [];
        var json = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? [] : (JsonNode.Parse(json) as JsonObject) ?? [];
    }

    private async Task SaveAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject EnsureObject(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        root[name] = created;
        return created;
    }
}
