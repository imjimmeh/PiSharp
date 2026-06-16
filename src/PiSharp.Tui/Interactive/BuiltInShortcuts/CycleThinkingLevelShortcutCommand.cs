using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class CycleThinkingLevelShortcutCommand : TuiBuiltInShortcutCommandBase
{
    private static readonly TuiHeaderHintDescriptor HeaderHintValue = new("thinking", "Ctrl+Y", 3);

    public override TuiKeybinding Binding { get; } = new("Ctrl+Y", "Cycle thinking level", "thinking-level", TuiShortcutAction.CycleThinkingLevel, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.Y.WithCtrl]);

    public override TuiHeaderHintDescriptor? HeaderHint => HeaderHintValue;

    public override void Execute(TuiShortcutContext context) => context.CycleThinkingLevel();
}
