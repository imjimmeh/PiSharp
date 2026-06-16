namespace PiSharp.Tui.Interactive;

public sealed record TuiCommandDescriptor(
    string Id,
    string Title,
    string? SlashCommand,
    string DefaultKeys,
    string Description,
    TuiShortcutAction ShortcutAction);
