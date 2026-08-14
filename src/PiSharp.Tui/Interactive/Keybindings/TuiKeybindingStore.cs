using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Keybindings;

/// <summary>
/// Client-side single owner of the effective keybinding table. Starts from built-in defaults and
/// re-derives everything when the user keybindings document is (re)loaded. Replaces the former
/// static <c>TuiShortcutRegistrar.GlobalActionsByKey</c> table and the
/// <c>TuiKeybindings</c> default-derived statics.
/// </summary>
public sealed class TuiKeybindingStore
{
    private readonly IReadOnlyList<TuiKeybinding> _defaults;
    private IReadOnlyList<TuiKeybinding> _effectiveBindings;
    private IReadOnlyList<string> _diagnostics = [];
    private IReadOnlyDictionary<Key, TuiShortcutAction> _globalActionsByKey = new Dictionary<Key, TuiShortcutAction>();
    private IReadOnlyList<TuiCommandDescriptor> _commandDescriptors = [];
    private IReadOnlyList<TuiHeaderHintDescriptor> _headerHints = [];
    private string _hotkeysText = string.Empty;

    public TuiKeybindingStore(IReadOnlyList<TuiKeybinding> defaults)
    {
        _defaults = defaults;
        _effectiveBindings = defaults.ToArray();
        RebuildDerived();
    }

    /// <summary>The effective binding table (defaults + user overrides).</summary>
    public IReadOnlyList<TuiKeybinding> EffectiveBindings => _effectiveBindings;

    /// <summary>Human-readable validation diagnostics from the last load.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Raised after the effective table is reloaded or rebuilt.</summary>
    public event Action? Changed;

    /// <summary>
    /// Resolves a key to its global shortcut action, honoring the remapped table including the
    /// ASCII control-code aliases Terminal.Gui delivers for Ctrl+letter shortcuts.
    /// </summary>
    public bool TryResolveGlobalAction(Key key, out TuiShortcutAction action)
    {
        if (_globalActionsByKey.TryGetValue(key, out action)) return true;
        if (!key.Handled) return false;
        return _globalActionsByKey.TryGetValue(new Key(key.KeyCode), out action);
    }

    /// <summary>Header hints (Ctrl+O etc.) with keys taken from the effective table.</summary>
    public IReadOnlyList<TuiHeaderHintDescriptor> HeaderHints => _headerHints;

    /// <summary>Command descriptors derived from the effective table (drives /hotkeys and /help).</summary>
    public IReadOnlyList<TuiCommandDescriptor> CommandDescriptors => _commandDescriptors;

    /// <summary>The /hotkeys text for the built-in (non-extension) bindings.</summary>
    public string HotkeysText() => _hotkeysText;

    /// <summary>Resolves an action id (e.g. "tools") to its effective key display string or the fallback.</summary>
    public string HintText(string action, string fallback)
        => _effectiveBindings.FirstOrDefault(binding => string.Equals(binding.Action, action, StringComparison.Ordinal))?.Keys ?? fallback;

    /// <summary>Reloads the effective table from a raw keybindings JSON document (may be null/empty).</summary>
    public void Reload(string? json)
    {
        var result = KeybindingsLoader.Merge(json, _defaults);
        _effectiveBindings = result.EffectiveBindings;
        _diagnostics = result.Diagnostics;
        RebuildDerived();
        Changed?.Invoke();
    }

    private void RebuildDerived()
    {
        _globalActionsByKey = BuildGlobalActionsByKey(_effectiveBindings);
        _commandDescriptors = _effectiveBindings
            .Select(binding => new TuiCommandDescriptor(
                binding.Action,
                binding.CommandTitle ?? binding.Description,
                binding.SlashCommand,
                binding.Keys,
                binding.Description,
                binding.ShortcutAction))
            .ToArray();
        _headerHints = BuildHeaderHints(_effectiveBindings);
        _hotkeysText = TuiHotkeyText.RenderFromDescriptors(_commandDescriptors, []);
    }

    private static IReadOnlyDictionary<Key, TuiShortcutAction> BuildGlobalActionsByKey(IEnumerable<TuiKeybinding> effective)
    {
        var actions = new Dictionary<Key, TuiShortcutAction>();
        foreach (var binding in effective.Where(binding => binding.RegistrationPolicy == TuiShortcutRegistrationPolicy.GlobalShortcut))
        {
            foreach (var key in binding.TerminalKeys.SelectMany(TuiShortcutKeyParser.ExpandTerminalKeyAliases))
            {
                // First wins; the loader already surfaced duplicate-key conflicts as diagnostics.
                actions.TryAdd(key, binding.ShortcutAction);
            }
        }
        return actions;
    }

    private static IReadOnlyList<TuiHeaderHintDescriptor> BuildHeaderHints(IEnumerable<TuiKeybinding> effective)
    {
        var byAction = effective
            .GroupBy(binding => binding.ShortcutAction)
            .ToDictionary(group => group.Key, group => group.First());

        return TuiBuiltInShortcutCatalog.Commands
            .Where(command => command.HeaderHint is not null)
            .Select(command => (command.Action, Hint: command.HeaderHint!))
            .Select(entry => new TuiHeaderHintDescriptor(
                entry.Hint.Label,
                byAction.TryGetValue(entry.Action, out var binding) ? binding.Keys : entry.Hint.Keys,
                entry.Hint.Order))
            .OrderBy(hint => hint.Order)
            .ToArray();
    }
}
