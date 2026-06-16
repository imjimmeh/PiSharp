using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Ai;
using PiSharp.Ai.Models;

namespace PiSharp.Runtime;

public sealed record RuntimeModelSelectionRequest(string? Provider, string? Model, ThinkingLevel? Thinking, IReadOnlyList<string>? ScopedModels = null);
public sealed record RuntimeModelSelection(ModelDescriptor Model, ThinkingLevel ThinkingLevel, IReadOnlyList<ModelDescriptor> ScopedModels, bool IsScoped);

public static class RuntimeModelSelector
{
    public static RuntimeModelSelection Resolve(RuntimeModelSelectionRequest request)
    {
        var scoped = ResolveScopedModels(request.ScopedModels);
        var (provider, modelPattern, thinkingFromModel) = SplitModelAndThinking(request.Model);
        if (provider is null && modelPattern is not null && modelPattern.Contains('/', StringComparison.Ordinal))
        {
            var parts = modelPattern.Split('/', 2, StringSplitOptions.TrimEntries);
            provider = parts[0];
            modelPattern = parts[1];
        }

        provider ??= request.Provider;
        var model = ResolveModel(provider, modelPattern, scoped)
            ?? scoped.FirstOrDefault()
            ?? PublicApi.Models.FirstOrDefault()?.Descriptor
            ?? throw new InvalidOperationException("No models are registered. Call PublicApi.RegisterBuiltInProviders() before model selection.");
        model = PublicApi.ResolveCatalogModel(model.Provider, model.Id);
        var requestedThinking = request.Thinking ?? thinkingFromModel ?? ThinkingLevel.Off;
        return new RuntimeModelSelection(model, ModelRegistry.ClampThinkingLevel(model, requestedThinking), scoped, scoped.Count > 0);
    }

    public static RuntimeModelSelection Cycle(RuntimeModelSelection current, int direction)
    {
        if (current.ScopedModels.Count == 0) return current;
        var index = current.ScopedModels.ToList().FindIndex(model => model.Provider == current.Model.Provider && model.Id == current.Model.Id);
        if (index < 0) index = 0;
        var next = current.ScopedModels[(index + direction + current.ScopedModels.Count) % current.ScopedModels.Count];
        return current with { Model = next, ThinkingLevel = ModelRegistry.ClampThinkingLevel(next, current.ThinkingLevel), IsScoped = true };
    }

    public static ThinkingLevel CycleThinking(ModelDescriptor model, ThinkingLevel current, int direction)
    {
        var levels = ModelRegistry.GetSupportedThinkingLevels(model);
        var index = levels.ToList().IndexOf(current);
        if (index < 0) index = 0;
        return levels[(index + direction + levels.Count) % levels.Count];
    }

    private static (string? Provider, string? Model, ThinkingLevel? Thinking) SplitModelAndThinking(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return (null, null, null);
        var colon = model.LastIndexOf(':');
        if (colon <= 0 || colon == model.Length - 1) return (null, model, null);
        var suffix = model[(colon + 1)..];
        return Enum.TryParse<ThinkingLevel>(suffix, ignoreCase: true, out var thinking)
            ? (null, model[..colon], thinking)
            : (null, model, null);
    }

    private static ModelDescriptor? ResolveModel(string? provider, string? pattern, IReadOnlyList<ModelDescriptor> scoped)
    {
        var candidates = scoped.Count > 0 ? scoped : PublicApi.Models.Select(m => m.Descriptor).ToArray();
        if (!string.IsNullOrWhiteSpace(provider)) candidates = candidates.Where(m => StringComparer.Ordinal.Equals(m.Provider, provider)).ToArray();
        if (string.IsNullOrWhiteSpace(pattern)) return candidates.FirstOrDefault();
        return candidates.FirstOrDefault(m => StringComparer.Ordinal.Equals(m.Id, pattern))
            ?? candidates.FirstOrDefault(m => m.Id.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(m => m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ModelDescriptor> ResolveScopedModels(IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return [];
        var resolved = new List<ModelDescriptor>();
        foreach (var pattern in patterns)
        {
            var (provider, model, _) = SplitModelAndThinking(pattern);
            if (provider is null && model is not null && model.Contains('/', StringComparison.Ordinal))
            {
                var parts = model.Split('/', 2, StringSplitOptions.TrimEntries);
                provider = parts[0];
                model = parts[1];
            }
            var descriptor = ResolveModel(provider, model, []) ?? throw new InvalidOperationException($"Unknown scoped model '{pattern}'.");
            resolved.Add(PublicApi.ResolveCatalogModel(descriptor.Provider, descriptor.Id));
        }
        return resolved;
    }
}
