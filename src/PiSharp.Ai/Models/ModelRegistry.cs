using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Models.Generated;

namespace PiSharp.Ai.Models;

public static class ModelRegistry
{
    public const string BuiltInSourceId = "built-in";
    public const string DynamicSourceId = "dynamic";

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Provider, string Id), List<OwnedModel>> Models = new();
    private static readonly Dictionary<string, List<OwnedProviderConfig>> ProviderConfigs = new(StringComparer.Ordinal);
    private static long _nextOrder;

    static ModelRegistry()
    {
        ResetToBuiltIns();
    }

    public static CatalogModel? GetModel(string provider, string id)
    {
        lock (Gate)
        {
            return ResolveModel((provider, id));
        }
    }

    public static IReadOnlyList<string> GetProviders()
    {
        lock (Gate)
        {
            return CurrentModels().Select(model => model.Provider).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }
    }

    public static IReadOnlyList<CatalogModel> GetModels(string provider)
    {
        lock (Gate)
        {
            return CurrentModels()
                .Where(m => StringComparer.Ordinal.Equals(m.Provider, provider))
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public static IReadOnlyList<CatalogModel> GetAllModels()
    {
        lock (Gate)
        {
            return CurrentModels()
                .OrderBy(m => m.Provider, StringComparer.Ordinal)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public static IReadOnlySet<string> GetCustomProviders()
    {
        lock (Gate)
        {
            return ProviderConfigs
                .Where(kvp => kvp.Value.Any(c => !StringComparer.Ordinal.Equals(c.SourceId, BuiltInSourceId)))
                .Select(kvp => kvp.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool IsProviderAccessible(
        string provider,
        IReadOnlySet<string> storedProviders,
        IReadOnlySet<string> customProviders)
    {
        if (customProviders.Contains(provider)) return true;
        if (storedProviders.Contains(provider)) return true;
        if (EnvApiKeyDetector.HasAmbientCredentials(provider)) return true;
        return false;
    }

    public static ModelProviderConfig? GetProviderConfig(string provider)
    {
        lock (Gate)
        {
            return ResolveProviderConfig(provider);
        }
    }

    public static IReadOnlyList<ModelProviderConfig> GetProviderConfigs()
    {
        lock (Gate)
        {
            return ProviderConfigs.Values
                .Select(ResolveProviderConfig)
                .Where(config => config is not null)
                .Cast<ModelProviderConfig>()
                .OrderBy(config => config.Provider, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public static void RegisterProviderConfig(ModelProviderConfig config, string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(config.Provider)) throw new ArgumentException("Provider is required.", nameof(config));
        var source = NormalizeSource(sourceId);
        lock (Gate)
        {
            RemoveProviderConfig(config.Provider, source);
            if (!ProviderConfigs.TryGetValue(config.Provider, out var configs))
            {
                configs = [];
                ProviderConfigs[config.Provider] = configs;
            }
            configs.Add(new OwnedProviderConfig(config, source, NextOrder()));
        }
    }

    public static void RegisterModel(CatalogModel model, string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(model.Provider)) throw new ArgumentException("Provider is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(model.Id)) throw new ArgumentException("Model id is required.", nameof(model));

        var source = NormalizeSource(sourceId);
        lock (Gate)
        {
            var key = (model.Provider, model.Id);
            RemoveModel(key, source);
            if (!Models.TryGetValue(key, out var models))
            {
                models = [];
                Models[key] = models;
            }
            models.Add(new OwnedModel(model, source, NextOrder()));
        }
    }

    public static void AddModel(CatalogModel model)
        => RegisterModel(model, DynamicSourceId);

    public static int UnregisterBySource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return 0;
        lock (Gate)
        {
            var removed = 0;
            foreach (var key in Models.Keys.ToArray())
            {
                var before = Models[key].Count;
                Models[key].RemoveAll(model => StringComparer.Ordinal.Equals(model.SourceId, sourceId));
                removed += before - Models[key].Count;
                if (Models[key].Count == 0) Models.Remove(key);
            }

            foreach (var provider in ProviderConfigs.Keys.ToArray())
            {
                var before = ProviderConfigs[provider].Count;
                ProviderConfigs[provider].RemoveAll(config => StringComparer.Ordinal.Equals(config.SourceId, sourceId));
                removed += before - ProviderConfigs[provider].Count;
                if (ProviderConfigs[provider].Count == 0) ProviderConfigs.Remove(provider);
            }

            return removed;
        }
    }

    public static void ResetToBuiltIns()
    {
        lock (Gate)
        {
            Models.Clear();
            ProviderConfigs.Clear();
            _nextOrder = 0;
            foreach (var model in BuiltInModels.All)
            {
                AddModelCore(model, BuiltInSourceId);
                AddProviderConfigCore(new ModelProviderConfig(
                    model.Provider,
                    model.Descriptor.Api,
                    model.Descriptor.BaseUrl,
                    model.Descriptor.ApiKey,
                    model.Descriptor.Headers,
                    model.Descriptor.AuthHeader), BuiltInSourceId);
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Models.Clear();
            ProviderConfigs.Clear();
        }
    }

    public static UsageCost CalculateCost(ModelDescriptor model, UsageInfo usage)
    {
        var cost = model.Cost;
        if (cost is null) return new UsageCost();

        var inputCost = cost.Input * usage.Input / 1_000_000m;
        var outputCost = cost.Output * usage.Output / 1_000_000m;
        var cacheReadCost = cost.CacheRead * usage.CacheRead / 1_000_000m;
        var cacheWriteCost = cost.CacheWrite * usage.CacheWrite / 1_000_000m;
        var total = inputCost + outputCost + cacheReadCost + cacheWriteCost;

        return new UsageCost(inputCost, outputCost, cacheReadCost, cacheWriteCost, total);
    }

    private static readonly ThinkingLevel[] ExtendedThinkingLevels =
        [ThinkingLevel.Off, ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh];

    public static IReadOnlyList<ThinkingLevel> GetSupportedThinkingLevels(ModelDescriptor model)
    {
        if (!model.Reasoning) return [ThinkingLevel.Off];

        var map = model.ThinkingLevelMap;
        if (map is null || map.Count == 0) return [ThinkingLevel.Off];

        var supported = new List<ThinkingLevel> { ThinkingLevel.Off };
        foreach (var level in ExtendedThinkingLevels.Skip(1))
        {
            var key = level.ToString().ToLowerInvariant();
            if (!map.TryGetValue(key, out var mapped)) continue;
            if (mapped < 0) continue;
            supported.Add(level);
        }

        return supported;
    }

    public static ThinkingLevel ClampThinkingLevel(ModelDescriptor model, ThinkingLevel requested)
    {
        var available = GetSupportedThinkingLevels(model);
        if (available.Contains(requested)) return requested;

        var requestedIndex = Array.IndexOf(ExtendedThinkingLevels, requested);
        if (requestedIndex < 0) return available.FirstOrDefault(ThinkingLevel.Off);

        for (var i = requestedIndex; i < ExtendedThinkingLevels.Length; i++)
        {
            if (available.Contains(ExtendedThinkingLevels[i])) return ExtendedThinkingLevels[i];
        }
        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (available.Contains(ExtendedThinkingLevels[i])) return ExtendedThinkingLevels[i];
        }

        return ThinkingLevel.Off;
    }

    private static IEnumerable<CatalogModel> CurrentModels()
        => Models.Values.Select(ResolveModel).Where(model => model is not null).Cast<CatalogModel>();

    private static CatalogModel? ResolveModel((string Provider, string Id) key)
        => Models.TryGetValue(key, out var models) ? ResolveModel(models) : null;

    private static CatalogModel? ResolveModel(List<OwnedModel> models)
        => models.OrderByDescending(model => model.Order).FirstOrDefault()?.Model;

    private static ModelProviderConfig? ResolveProviderConfig(string provider)
        => ProviderConfigs.TryGetValue(provider, out var configs) ? ResolveProviderConfig(configs) : null;

    private static ModelProviderConfig? ResolveProviderConfig(List<OwnedProviderConfig> configs)
        => configs.OrderByDescending(config => config.Order).FirstOrDefault()?.Config;

    private static void RemoveModel((string Provider, string Id) key, string sourceId)
    {
        if (!Models.TryGetValue(key, out var models)) return;
        models.RemoveAll(model => StringComparer.Ordinal.Equals(model.SourceId, sourceId));
        if (models.Count == 0) Models.Remove(key);
    }

    private static void RemoveProviderConfig(string provider, string sourceId)
    {
        if (!ProviderConfigs.TryGetValue(provider, out var configs)) return;
        configs.RemoveAll(config => StringComparer.Ordinal.Equals(config.SourceId, sourceId));
        if (configs.Count == 0) ProviderConfigs.Remove(provider);
    }

    private static void AddModelCore(CatalogModel model, string sourceId)
    {
        var key = (model.Provider, model.Id);
        if (!Models.TryGetValue(key, out var models))
        {
            models = [];
            Models[key] = models;
        }
        models.Add(new OwnedModel(model, sourceId, NextOrder()));
    }

    private static void AddProviderConfigCore(ModelProviderConfig config, string sourceId)
    {
        if (!ProviderConfigs.TryGetValue(config.Provider, out var configs))
        {
            configs = [];
            ProviderConfigs[config.Provider] = configs;
        }
        configs.Add(new OwnedProviderConfig(config, sourceId, NextOrder()));
    }

    private static long NextOrder() => ++_nextOrder;

    private static string NormalizeSource(string? sourceId)
        => string.IsNullOrWhiteSpace(sourceId) ? DynamicSourceId : sourceId;

    private sealed record OwnedModel(CatalogModel Model, string SourceId, long Order);
    private sealed record OwnedProviderConfig(ModelProviderConfig Config, string SourceId, long Order);
}
