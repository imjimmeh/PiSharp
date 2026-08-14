using System.Text.Json;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Keybindings;

/// <summary>
/// Parses the <c>~/.pi/agent/keybindings.json</c> document and merges it over the built-in
/// default bindings to produce an effective table. Validation is non-fatal: every problem is
/// reported as a diagnostic and the affected entry falls back to the built-in default.
/// </summary>
public static class KeybindingsLoader
{
    private const string RootSection = "keybindings";
    private const string KeySeparator = "/";

    /// <summary>
    /// Merges <paramref name="json"/> (the raw file contents) over <paramref name="defaults"/>.
    /// A <c>null</c>/whitespace input or a parse failure leaves the defaults unchanged.
    /// </summary>
    public static KeybindingsLoadResult Merge(string? json, IReadOnlyList<TuiKeybinding> defaults)
    {
        var diagnostics = new List<string>();
        var effective = defaults.ToList();

        if (string.IsNullOrWhiteSpace(json))
            return new KeybindingsLoadResult(effective, diagnostics);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"Failed to parse keybindings: {ex.Message}");
            return new KeybindingsLoadResult(effective, diagnostics);
        }

        using (document)
        {
            if (!TryGetKeybindingsSection(document, out var root))
            {
                diagnostics.Add("keybindings file is missing a top-level \"keybindings\" object; using built-in defaults.");
                return new KeybindingsLoadResult(effective, diagnostics);
            }

            // Keys currently claimed by a binding, seeded from the built-in defaults so a user
            // remap cannot silently collide with a still-active default key (first wins).
            var claimed = BuildClaimedMap(defaults);

            foreach (var property in root.EnumerateObject())
            {
                var actionId = property.Name.Trim();
                var matched = defaults
                    .Where(binding => MatchesActionId(binding, actionId))
                    .ToArray();

                if (matched.Length == 0)
                {
                    diagnostics.Add($"Unknown keybinding action '{property.Name}' was ignored.");
                    continue;
                }

                var primary = matched[0];
                var actions = matched.Select(binding => binding.ShortcutAction).Distinct().ToArray();

                if (IsUnbind(property.Value))
                {
                    ReleaseClaims(effective, claimed, actions);
                    effective.RemoveAll(binding => actions.Contains(binding.ShortcutAction));
                    continue;
                }

                var keyStrings = ParseKeyStrings(property.Value, actionId, diagnostics);
                if (keyStrings.Count == 0)
                {
                    // Unparseable entries keep the built-in keys (the action stays unchanged).
                    continue;
                }

                // The remap applies: release this action's current claims, then claim the new keys.
                ReleaseClaims(effective, claimed, actions);
                effective.RemoveAll(binding => actions.Contains(binding.ShortcutAction));

                var survivingKeys = new List<Key>();
                foreach (var keyString in keyStrings)
                {
                    if (!TuiShortcutKeyParser.TryParse(keyString, out var terminalKeys)) continue;
                    foreach (var alias in terminalKeys.SelectMany(TuiShortcutKeyParser.ExpandTerminalKeyAliases))
                    {
                        if (claimed.TryGetValue(alias, out var existingAction))
                        {
                            if (existingAction == primary.ShortcutAction) continue;
                            diagnostics.Add($"Key '{keyString}' is already bound to '{existingAction}' and was ignored for '{actionId}'.");
                            continue;
                        }
                        survivingKeys.Add(alias);
                        claimed[alias] = primary.ShortcutAction;
                    }
                }

                if (survivingKeys.Count == 0)
                    continue;

                var displayKeys = property.Value.ValueKind == JsonValueKind.Array
                    ? string.Join(KeySeparator, keyStrings)
                    : keyStrings[0];

                effective.Add(new TuiKeybinding(
                    displayKeys,
                    primary.Description,
                    primary.Action,
                    primary.ShortcutAction,
                    primary.Scope,
                    primary.RegistrationPolicy,
                    survivingKeys.Distinct().ToArray(),
                    primary.SlashCommand,
                    primary.CommandTitle));
            }
        }

        return new KeybindingsLoadResult(effective, diagnostics);
    }

    /// <summary>Loads and merges the file at <paramref name="path"/> over the defaults.</summary>
    public static KeybindingsLoadResult LoadFile(string path, IReadOnlyList<TuiKeybinding> defaults)
    {
        if (!TryReadFile(path, out var json, out var error))
            return new KeybindingsLoadResult(defaults.ToList(), string.IsNullOrEmpty(error) ? [] : [error]);

        return Merge(json, defaults);
    }

    /// <summary>Reads the file's raw text; returns false (with a diagnostic-filled error) on failure.</summary>
    public static bool TryReadFile(string path, out string json, out string? error)
    {
        json = string.Empty;
        error = null;
        try
        {
            json = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"Failed to read keybindings file '{path}': {ex.Message}";
            return false;
        }
    }

    private static bool TryGetKeybindingsSection(JsonDocument document, out JsonElement root)
    {
        root = default;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        if (!document.RootElement.TryGetProperty(RootSection, out var section) || section.ValueKind != JsonValueKind.Object)
            return false;
        root = section;
        return true;
    }

    private static bool MatchesActionId(TuiKeybinding binding, string actionId)
        => string.Equals(binding.Action, actionId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(binding.ShortcutAction.ToString(), actionId, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ParseKeyStrings(JsonElement value, string actionId, List<string> diagnostics)
    {
        var result = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                AddKeyString(value.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String) AddKeyString(element.GetString());
                    else diagnostics.Add($"Invalid key list entry for '{actionId}': expected a string.");
                }
                break;
            default:
                diagnostics.Add($"Invalid value for '{actionId}': expected a key string, an array, or null.");
                break;
        }

        return result;

        void AddKeyString(string? keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return;
            if (!TuiShortcutKeyParser.TryParse(keyString, out _))
            {
                diagnostics.Add($"Invalid key string '{keyString}' for '{actionId}' was ignored.");
                return;
            }
            result.Add(keyString.Trim());
        }
    }

    private static bool IsUnbind(JsonElement value)
        => value.ValueKind == JsonValueKind.Null
           || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
           || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static void ReleaseClaims(List<TuiKeybinding> effective, Dictionary<Key, TuiShortcutAction> claimed, IReadOnlyList<TuiShortcutAction> actions)
    {
        foreach (var binding in effective.Where(binding => actions.Contains(binding.ShortcutAction)))
        {
            foreach (var key in binding.TerminalKeys)
            {
                foreach (var alias in TuiShortcutKeyParser.ExpandTerminalKeyAliases(key))
                {
                    if (claimed.TryGetValue(alias, out var claimant) && claimant == binding.ShortcutAction)
                        claimed.Remove(alias);
                }
            }
        }
    }

    private static Dictionary<Key, TuiShortcutAction> BuildClaimedMap(IReadOnlyList<TuiKeybinding> defaults)
    {
        var claimed = new Dictionary<Key, TuiShortcutAction>();
        foreach (var binding in defaults)
        {
            foreach (var key in binding.TerminalKeys)
            {
                foreach (var alias in TuiShortcutKeyParser.ExpandTerminalKeyAliases(key))
                {
                    claimed.TryAdd(alias, binding.ShortcutAction);
                }
            }
        }
        return claimed;
    }
}
