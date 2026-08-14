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
        // @role expansion — first step, before the provider/model split.
        EffortPreset? roleEffort = null;
        ThinkingLevel? roleRequestSuffixThinking = null;
        string? modelToken = request.Model;

        if (modelToken is not null && modelToken.StartsWith('@'))
        {
            // Extract :thinking suffix from @role:high before expansion.
            var (_, rolePattern, suffixThinking) = SplitModelAndThinking(modelToken);
            roleRequestSuffixThinking = suffixThinking;
            var patternToExpand = rolePattern ?? modelToken;
            var (expandedModel, effort) = ExpandRole(patternToExpand, depth: 0, visited: []);
            modelToken = expandedModel;
            roleEffort = effort;
        }

        var scoped = ResolveScopedModels(request.ScopedModels);
        var (provider, modelPattern, thinkingFromModel) = SplitModelAndThinking(modelToken);
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

        // Budget overrides (role requests only) — applied before clamping so supported levels reflect the override.
        if (roleEffort?.Budgets is not null)
            model = MergeBudgets(model, roleEffort.Budgets);

        // Precedence: request.Thinking > :thinking suffix from @role:high > :thinking from role selector > effort preset > Off.
        var requestedThinking = request.Thinking
            ?? roleRequestSuffixThinking
            ?? thinkingFromModel
            ?? roleEffort?.ThinkingLevel
            ?? ThinkingLevel.Off;
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

    private const int MaxRoleDepth = 8;

    private static (string Model, EffortPreset? Effort) ExpandRole(string token, int depth, HashSet<string> visited)
    {
        if (depth > MaxRoleDepth)
            throw new InvalidOperationException($"Model role expansion exceeded max depth of {MaxRoleDepth}. Possible circular role reference.");

        var name = token.Trim();
        if (name.StartsWith('@')) name = name[1..];
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Empty model role name. Define it in settings 'modelRoles' or load the PiSharp.ModelRoles plugin.");

        var normalizedName = name.ToLowerInvariant();
        if (visited.Contains(normalizedName))
            throw new InvalidOperationException($"Circular model role reference detected: '@{normalizedName}'.");

        var resolution = ModelRoleRegistry.Resolve(normalizedName)
            ?? throw new InvalidOperationException($"Unknown model role '@{normalizedName}'. Define it in settings 'modelRoles' or load the PiSharp.ModelRoles plugin.");

        var newVisited = new HashSet<string>(visited, StringComparer.Ordinal) { normalizedName };

        foreach (var selector in resolution.Selectors)
        {
            var candidate = selector.Trim();
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            if (candidate.StartsWith('@'))
            {
                try
                {
                    var (expandedModel, effort) = ExpandRole(candidate, depth + 1, newVisited);
                    return (expandedModel, resolution.Effort ?? effort);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
            }

            // Direct selector — verify it resolves to a model.
            var (prov, pat, _) = SplitModelAndThinking(candidate);
            if (prov is null && pat is not null && pat.Contains('/', StringComparison.Ordinal))
            {
                var parts = pat.Split('/', 2, StringSplitOptions.TrimEntries);
                prov = parts[0];
                pat = parts[1];
            }
            if (ResolveModel(prov, pat, []) is not null)
                return (candidate, resolution.Effort);
        }

        throw new InvalidOperationException($"Model role '@{normalizedName}' has no resolvable candidates.");
    }

    private static ModelDescriptor MergeBudgets(ModelDescriptor model, IReadOnlyDictionary<string, int> budgets)
    {
        var baseMap = model.ThinkingLevelMap ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, int>(baseMap, StringComparer.OrdinalIgnoreCase);
        foreach (var (level, budget) in budgets)
        {
            var key = level.ToLowerInvariant();
            merged[key] = budget;
        }
        return model with { ThinkingLevelMap = merged };
    }
}
