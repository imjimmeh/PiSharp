using PiSharp.Abstractions.Options;

namespace PiSharp.Agent.Core.Models;

/// <summary>
/// A named effort preset: a thinking-level default and optional per-level
/// token-budget overrides that are merged over the model's native
/// <see cref="ModelDescriptor.ThinkingLevelMap"/>.
/// </summary>
public sealed record EffortPreset(
    ThinkingLevel? ThinkingLevel,
    IReadOnlyDictionary<string, int>? Budgets);

/// <summary>
/// The resolution of a named model role: an ordered list of candidate
/// selectors and an optional effort preset.
/// </summary>
public sealed record ModelRoleResolution(
    string Role,
    IReadOnlyList<string> Selectors,
    EffortPreset? Effort);

/// <summary>
/// Implemented by a plugin that maps role names to resolutions.
/// The concrete settings-backed resolver ships as a separate plugin.
/// </summary>
public interface IModelRoleResolver
{
    string SourceId { get; }
    ModelRoleResolution? Resolve(string role);
    IReadOnlyList<string> Roles { get; }
}

/// <summary>
/// Static first-wins registry of <see cref="IModelRoleResolver"/> instances.
/// Resolution tries resolvers in registration order; the first that knows
/// the role wins. Later registrations for the same role are ignored.
/// </summary>
public static class ModelRoleRegistry
{
    private static readonly List<IModelRoleResolver> _resolvers = [];
    private static readonly object _gate = new();

    public static void Register(IModelRoleResolver resolver)
    {
        lock (_gate) { _resolvers.Add(resolver); }
    }

    public static bool Unregister(string sourceId)
    {
        lock (_gate)
        {
            return _resolvers.RemoveAll(r => string.Equals(r.SourceId, sourceId, StringComparison.Ordinal)) > 0;
        }
    }

    public static ModelRoleResolution? Resolve(string role)
    {
        lock (_gate)
        {
            foreach (var resolver in _resolvers)
            {
                var resolution = resolver.Resolve(role);
                if (resolution is not null) return resolution;
            }
        }
        return null;
    }

    public static IReadOnlyList<string> Roles
    {
        get
        {
            lock (_gate)
            {
                return _resolvers.SelectMany(r => r.Roles).Distinct().ToList();
            }
        }
    }

    public static void Clear()
    {
        lock (_gate) { _resolvers.Clear(); }
    }
}
