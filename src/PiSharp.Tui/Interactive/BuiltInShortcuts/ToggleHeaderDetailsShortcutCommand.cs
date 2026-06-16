using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ToggleHeaderDetailsShortcutCommand : TuiBuiltInShortcutCommandBase
{
    private static readonly TuiHeaderHintDescriptor HeaderHintValue = new("details", "Ctrl+H", 4);

    public override TuiKeybinding Binding { get; } = new("Ctrl+H", "Toggle header details", "header", TuiShortcutAction.ToggleHeaderDetails, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.H.WithCtrl]);

    public override TuiHeaderHintDescriptor? HeaderHint => HeaderHintValue;

    public override void Execute(TuiShortcutContext context) => context.ToggleHeaderDetails();
}
