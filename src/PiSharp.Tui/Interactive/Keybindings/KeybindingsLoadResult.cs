namespace PiSharp.Tui.Interactive.Keybindings;

/// <summary>
/// The result of merging a user keybindings document over the built-in defaults:
/// the effective binding table plus any human-readable validation diagnostics.
/// </summary>
public sealed record KeybindingsLoadResult(
    IReadOnlyList<TuiKeybinding> EffectiveBindings,
    IReadOnlyList<string> Diagnostics);
