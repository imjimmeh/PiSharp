using PiSharp.Agent.Core.Models;

namespace PiSharp.ModelRoles;

/// <summary>
/// Settings-backed <see cref="IModelRoleResolver"/>: serves the normalized
/// role → <see cref="ModelRoleResolution"/> map parsed from the
/// <c>modelRoles</c>/<c>effort</c> settings sections.
/// </summary>
public sealed class SettingsModelRoleResolver : IModelRoleResolver
{
    private readonly IReadOnlyDictionary<string, ModelRoleResolution> _roles;

    public SettingsModelRoleResolver(string sourceId, IReadOnlyDictionary<string, ModelRoleResolution> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(roles);
        SourceId = sourceId;
        _roles = roles;
    }

    /// <inheritdoc />
    public string SourceId { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => _roles.Keys.ToArray();

    /// <inheritdoc />
    public ModelRoleResolution? Resolve(string role)
    {
        var name = ModelRolesSettingsParser.NormalizeRole(role);
        return _roles.TryGetValue(name, out var resolution) ? resolution : null;
    }
}
