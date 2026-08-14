using System.Text.Json;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;

namespace PiSharp.ModelRoles;

/// <summary>
/// Pure JSON-to-model parser for the <c>modelRoles</c> and <c>effort</c> settings
/// maps. Consumes two optional <see cref="JsonElement"/> sections and produces a
/// normalized role-name → <see cref="ModelRoleResolution"/> map. Invalid entries
/// are reported through <paramref name="diagnostic"/> and skipped; other roles are
/// unaffected.
/// </summary>
public static class ModelRolesSettingsParser
{
    /// <summary>Normalizes a role name: strips a leading '@' and lowercases.</summary>
    public static string NormalizeRole(string role)
        => role.TrimStart('@').Trim().ToLowerInvariant();

    /// <summary>
    /// Parses the top-level <c>modelRoles</c> and <c>effort</c> settings sections into
    /// a dictionary keyed by normalized role name.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelRoleResolution> Parse(
        JsonElement? modelRoles,
        JsonElement? effort,
        Action<string>? diagnostic = null)
    {
        var effortPresets = ParseEffortPresets(effort, diagnostic);

        var result = new Dictionary<string, ModelRoleResolution>(StringComparer.Ordinal);

        if (modelRoles is not { } rolesElement || rolesElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in rolesElement.EnumerateObject())
        {
            var roleName = NormalizeRole(property.Name);
            if (roleName.Length == 0)
            {
                Report(diagnostic, $"Skipping model role '{property.Name}': role name is empty.");
                continue;
            }

            var resolution = ParseRole(property.Value, roleName, effortPresets, diagnostic);
            if (resolution is not null)
            {
                result[roleName] = resolution;
            }
        }

        return result;
    }

    private static ModelRoleResolution? ParseRole(
        JsonElement value,
        string roleName,
        IReadOnlyDictionary<string, EffortPreset> effortPresets,
        Action<string>? diagnostic)
    {
        IReadOnlyList<string>? selectors;
        EffortPreset? effort = null;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                selectors = SingleSelector(value.GetString(), diagnostic);
                break;

            case JsonValueKind.Array:
                selectors = ParseSelectorArray(value, diagnostic);
                break;

            case JsonValueKind.Object:
                if (!value.TryGetProperty("models", out var modelsProp))
                {
                    Report(diagnostic, $"Skipping model role '@{roleName}': object form requires a 'models' property (selector string or array).");
                    return null;
                }
                selectors = modelsProp.ValueKind switch
                {
                    JsonValueKind.String => SingleSelector(modelsProp.GetString(), diagnostic),
                    JsonValueKind.Array => ParseSelectorArray(modelsProp, diagnostic),
                    _ => null
                };

                if (value.TryGetProperty("effort", out var effortProp))
                {
                    if (effortProp.ValueKind == JsonValueKind.String &&
                        effortProp.GetString() is { Length: > 0 } presetName)
                    {
                        if (effortPresets.TryGetValue(NormalizeRole(presetName), out var preset))
                        {
                            effort = preset;
                        }
                        else
                        {
                            Report(diagnostic, $"Role '@{roleName}' references unknown effort preset '{presetName}'; resolving with no effort.");
                        }
                    }
                    else
                    {
                        Report(diagnostic, $"Role '@{roleName}' has a non-string 'effort'; expected a preset name. Resolving with no effort.");
                    }
                }
                break;

            default:
                Report(diagnostic, $"Skipping model role '@{roleName}': value must be a selector string, an array of selectors, or an object with 'models' and optional 'effort'.");
                return null;
        }

        if (selectors is null || selectors.Count == 0)
        {
            Report(diagnostic, $"Skipping model role '@{roleName}': no valid selectors.");
            return null;
        }

        return new ModelRoleResolution(roleName, selectors, effort);
    }

    private static IReadOnlyList<string>? SingleSelector(string? selector, Action<string>? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;
        var trimmed = selector!.Trim();
        if (!IsValidSelector(trimmed, out var reason))
        {
            Report(diagnostic, $"Ignoring invalid model selector '{trimmed}': {reason}.");
            return null;
        }
        return [trimmed];
    }

    private static IReadOnlyList<string>? ParseSelectorArray(JsonElement array, Action<string>? diagnostic)
    {
        var selectors = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                var selector = item.GetString()!.Trim();
                if (IsValidSelector(selector, out var reason))
                {
                    // Prioritized candidates: keep declaration order, drop duplicates.
                    if (!selectors.Contains(selector, StringComparer.Ordinal))
                    {
                        selectors.Add(selector);
                    }
                }
                else
                {
                    Report(diagnostic, $"Ignoring invalid model selector '{selector}': {reason}.");
                }
            }
            else
            {
                Report(diagnostic, "Ignoring invalid selector in modelRoles: expected a non-empty string.");
            }
        }
        return selectors;
    }

    /// <summary>
    /// Validates a selector against the documented shapes: <c>@role</c>,
    /// <c>provider/id</c>, or <c>provider/id:thinking</c> where <c>thinking</c> is a
    /// <see cref="ThinkingLevel"/> name. Returns false with a reason for anything else.
    /// </summary>
    private static bool IsValidSelector(string selector, out string? reason)
    {
        if (selector.StartsWith('@'))
        {
            if (selector.Length <= 1 || string.IsNullOrWhiteSpace(selector[1..]))
            {
                reason = "a nested '@role' selector must name a role";
                return false;
            }
            reason = null;
            return true;
        }

        // Optional ':thinking' suffix (split on the last colon, mirroring
        // RuntimeModelSelector); a present suffix MUST be a thinking level.
        var colon = selector.LastIndexOf(':');
        if (colon > 0 && colon < selector.Length - 1)
        {
            var suffix = selector[(colon + 1)..];
            if (!Enum.TryParse<ThinkingLevel>(suffix, ignoreCase: true, out _))
            {
                reason = $"unknown ':thinking' suffix '{suffix}'; expected a thinking level (off, minimal, low, medium, high, xhigh)";
                return false;
            }
            return IsProviderId(selector[..colon], out reason);
        }

        return IsProviderId(selector, out reason);
    }

    private static bool IsProviderId(string value, out string? reason)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
        {
            reason = "selector must be 'provider/id', 'provider/id:thinking', or '@role'";
            return false;
        }
        reason = null;
        return true;
    }

    private static IReadOnlyDictionary<string, EffortPreset> ParseEffortPresets(
        JsonElement? effort,
        Action<string>? diagnostic)
    {
        var presets = new Dictionary<string, EffortPreset>(StringComparer.Ordinal);
        if (effort is not { } effortElement || effortElement.ValueKind != JsonValueKind.Object)
        {
            return presets;
        }

        foreach (var property in effortElement.EnumerateObject())
        {
            var name = NormalizeRole(property.Name);
            if (name.Length == 0)
            {
                Report(diagnostic, $"Skipping effort preset '{property.Name}': name is empty.");
                continue;
            }

            EffortPreset? preset = property.Value.ValueKind switch
            {
                JsonValueKind.String => ParseEffortFromString(property.Value.GetString(), diagnostic),
                JsonValueKind.Object => ParseEffortFromObject(property.Value, diagnostic),
                _ => null
            };

            if (preset is null)
            {
                Report(diagnostic, $"Skipping effort preset '{property.Name}': value must be a thinking-level name or an object with 'thinkingLevel' and/or 'budgets'.");
                continue;
            }

            presets[name] = preset;
        }

        return presets;
    }

    private static EffortPreset? ParseEffortFromString(string? thinkingLevelName, Action<string>? diagnostic)
    {
        if (!TryParseThinkingLevel(thinkingLevelName, out var level))
        {
            Report(diagnostic, $"Skipping effort preset '{thinkingLevelName}': not a valid thinking level.");
            return null;
        }
        return new EffortPreset(level, null);
    }

    private static EffortPreset? ParseEffortFromObject(JsonElement value, Action<string>? diagnostic)
    {
        ThinkingLevel? level = null;
        if (value.TryGetProperty("thinkingLevel", out var levelProp))
        {
            if (levelProp.ValueKind == JsonValueKind.String &&
                TryParseThinkingLevel(levelProp.GetString(), out var parsed))
            {
                level = parsed;
            }
            else if (levelProp.ValueKind != JsonValueKind.Null)
            {
                Report(diagnostic, "Ignoring invalid 'thinkingLevel' in effort preset: expected a thinking-level name.");
            }
        }

        IReadOnlyDictionary<string, int>? budgets = null;
        if (value.TryGetProperty("budgets", out var budgetsProp) &&
            budgetsProp.ValueKind == JsonValueKind.Object)
        {
            budgets = ParseBudgets(budgetsProp, diagnostic);
        }

        return new EffortPreset(level, budgets);
    }

    private static IReadOnlyDictionary<string, int>? ParseBudgets(JsonElement budgets, Action<string>? diagnostic)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in budgets.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt64(out var raw) ||
                raw <= 0)
            {
                Report(diagnostic, $"Ignoring invalid effort budget '{property.Name}': expected a positive integer.");
                continue;
            }
            var levelName = NormalizeRole(property.Name);
            if (levelName.Length == 0)
            {
                Report(diagnostic, "Ignoring effort budget with empty thinking-level name.");
                continue;
            }
            map[levelName] = (int)raw;
        }
        return map;
    }

    private static bool TryParseThinkingLevel(string? name, out ThinkingLevel level)
    {
        if (!string.IsNullOrWhiteSpace(name) &&
            Enum.TryParse(name, ignoreCase: true, out level))
        {
            return true;
        }
        level = default;
        return false;
    }

    private static void Report(Action<string>? diagnostic, string message)
        => diagnostic?.Invoke(message);
}
