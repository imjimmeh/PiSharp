using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ToggleToolOutputShortcutCommand : TuiBuiltInShortcutCommandBase
{
    private static readonly TuiHeaderHintDescriptor HeaderHintValue = new("tools", "Ctrl+O", 1);

    public override TuiKeybinding Binding { get; } = new("Ctrl+O", "Toggle tool output", "tools", TuiShortcutAction.ToggleToolOutput, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.O.WithCtrl]);

    public override TuiHeaderHintDescriptor? HeaderHint => HeaderHintValue;

    public override void Execute(TuiShortcutContext context) => context.ToggleToolOutput();
}
