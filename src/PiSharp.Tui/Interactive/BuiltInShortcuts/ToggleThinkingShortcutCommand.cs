using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ToggleThinkingShortcutCommand : TuiBuiltInShortcutCommandBase
{
    private static readonly TuiHeaderHintDescriptor HeaderHintValue = new("thoughts", "Ctrl+T", 2);

    public override TuiKeybinding Binding { get; } = new("Ctrl+T", "Toggle thinking blocks", "thinking", TuiShortcutAction.ToggleThinking, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.T.WithCtrl]);

    public override TuiHeaderHintDescriptor? HeaderHint => HeaderHintValue;

    public override void Execute(TuiShortcutContext context) => context.ToggleThinking();
}
