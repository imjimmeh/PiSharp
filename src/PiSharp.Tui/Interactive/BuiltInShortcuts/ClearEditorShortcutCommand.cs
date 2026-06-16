using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ClearEditorShortcutCommand : TuiBuiltInShortcutCommandBase
{
    public override TuiKeybinding Binding { get; } = new("Ctrl+C/Ctrl+U", "Clear editor", "clear-editor", TuiShortcutAction.ClearEditor, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.C.WithCtrl, Key.U.WithCtrl], SlashCommand: "/clear", CommandTitle: "Clear transcript");

    public override void Execute(TuiShortcutContext context) => context.CtrlC();
}
