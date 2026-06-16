using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class OpenModelSelectorShortcutCommand : TuiBuiltInShortcutCommandBase
{
    private static readonly TuiHeaderHintDescriptor HeaderHintValue = new("model", "Ctrl+L", 0);

    public override TuiKeybinding Binding { get; } = new("Ctrl+L", "Open model selector", "model", TuiShortcutAction.OpenModelSelector, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.L.WithCtrl], SlashCommand: "/model", CommandTitle: "Open model selector");

    public override TuiHeaderHintDescriptor? HeaderHint => HeaderHintValue;

    public override void Execute(TuiShortcutContext context) => context.OpenModelSelector();
}
