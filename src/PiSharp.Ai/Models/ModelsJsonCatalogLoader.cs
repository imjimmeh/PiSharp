using System.Text.Json;
using PiSharp.Agent.Core.Models;

namespace PiSharp.Ai.Models;

public static class ModelsJsonCatalogLoader
{
    public static int Load(string? path, string sourceId = "models.json")
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!document.RootElement.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object) return 0;

        var count = 0;
        foreach (var provider in providers.EnumerateObject())
        {
            count += RegisterProvider(provider.Name, provider.Value, sourceId);
        }

        return count;
    }

    private static int RegisterProvider(string providerName, JsonElement element, string sourceId)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Provider '{providerName}' in models.json must be an object.");

        var api = GetString(element, "api");
        var baseUrl = GetString(element, "baseUrl");
        var apiKey = GetString(element, "apiKey");
        var authHeader = GetBool(element, "authHeader");
        var headers = GetStringMap(element, "headers");
        var config = new ModelProviderConfig(providerName, api, baseUrl, apiKey, headers, authHeader);
        ModelRegistry.RegisterProviderConfig(config, sourceId);
        var registered = 1;

        if (element.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0)
        {
            foreach (var model in models.EnumerateArray())
            {
                ModelRegistry.RegisterModel(ToCatalogModel(providerName, config, model), sourceId);
                registered++;
            }
            return registered;
        }

        if (HasModelOverride(config))
        {
            foreach (var existing in ModelRegistry.GetModels(providerName))
            {
                ModelRegistry.RegisterModel(new CatalogModel(existing.Provider, existing.Id, ApplyProviderOverride(existing.Descriptor, config)), sourceId);
                registered++;
            }
        }

        return registered;
    }

    private static CatalogModel ToCatalogModel(string providerName, ModelProviderConfig config, JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Model entry for provider '{providerName}' must be an object.");

        var id = GetString(model, "id") ?? throw new InvalidOperationException($"Model entry for provider '{providerName}' is missing required 'id'.");
        var modelProvider = GetString(model, "provider") ?? providerName;
        var api = GetString(model, "api") ?? config.Api ?? throw new InvalidOperationException($"Model '{modelProvider}/{id}' is missing required provider api.");
        var baseUrl = GetString(model, "baseUrl") ?? config.BaseUrl ?? string.Empty;
        var headers = Merge(config.Headers, GetStringMap(model, "headers"));
        var descriptor = new ModelDescriptor(
            Provider: modelProvider,
            Id: id,
            Api: api,
            Name: GetString(model, "name") ?? id,
            BaseUrl: baseUrl,
            Reasoning: GetBool(model, "reasoning") ?? false,
            ContextWindow: GetInt(model, "contextWindow") ?? 0,
            MaxTokens: GetInt(model, "maxTokens") ?? 0,
            ThinkingLevelMap: GetIntMap(model, "thinkingLevelMap"),
            Input: GetStringArray(model, "input"),
            Cost: GetCost(model),
            Headers: headers,
            ApiKey: GetString(model, "apiKey") ?? config.ApiKey,
            AuthHeader: GetBool(model, "authHeader") ?? config.AuthHeader);

        return new CatalogModel(modelProvider, id, descriptor);
    }

    private static ModelDescriptor ApplyProviderOverride(ModelDescriptor descriptor, ModelProviderConfig config)
        => descriptor with
        {
            Api = config.Api ?? descriptor.Api,
            BaseUrl = config.BaseUrl ?? descriptor.BaseUrl,
            Headers = Merge(descriptor.Headers, config.Headers),
            ApiKey = config.ApiKey ?? descriptor.ApiKey,
            AuthHeader = config.AuthHeader ?? descriptor.AuthHeader
        };

    private static bool HasModelOverride(ModelProviderConfig config)
        => config.Api is not null || config.BaseUrl is not null || config.ApiKey is not null || config.Headers is not null || config.AuthHeader is not null;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;
    }

    private static IReadOnlyDictionary<string, string>? GetStringMap(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text) map[property.Name] = text;
        }
        return map.Count == 0 ? null : map;
    }

    private static IReadOnlyDictionary<string, int>? GetIntMap(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number)) map[property.Name] = number;
        }
        return map.Count == 0 ? null : map;
    }

    private static IReadOnlyList<string>? GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var values = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static ModelCost? GetCost(JsonElement element)
    {
        if (!element.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) return null;
        return new ModelCost(GetDecimal(cost, "input"), GetDecimal(cost, "output"), GetDecimal(cost, "cacheRead"), GetDecimal(cost, "cacheWrite"));
    }

    private static decimal GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result) ? result : 0;
    }

    private static IReadOnlyDictionary<string, string>? Merge(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second)
    {
        if (first is null && second is null) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (first is not null)
        {
            foreach (var item in first) result[item.Key] = item.Value;
        }
        if (second is not null)
        {
            foreach (var item in second) result[item.Key] = item.Value;
        }
        return result;
    }
}
