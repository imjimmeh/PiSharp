namespace PiSharp.Tui.Interactive.Keybindings;

/// <summary>
/// Parsed representation of the user keybindings file (<c>PiAgentPaths.KeybindingsPath</c>).
/// Values are the keys bound to each action id; a <c>null</c> or empty entry means
/// <c>unbind</c> (the action loses all keys). See plan 4.3 for the JSON schema.
/// </summary>
public sealed record KeybindingsDocument(IReadOnlyDictionary<string, string[]?> Keybindings);
