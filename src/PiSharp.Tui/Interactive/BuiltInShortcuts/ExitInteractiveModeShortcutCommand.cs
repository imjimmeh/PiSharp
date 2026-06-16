using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ExitInteractiveModeShortcutCommand : TuiBuiltInShortcutCommandBase
{
    public override TuiKeybinding Binding { get; } = new("Ctrl+D", "Exit interactive mode", "exit", TuiShortcutAction.ExitInteractiveMode, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.D.WithCtrl], SlashCommand: "/exit", CommandTitle: "Exit interactive mode");

    public override void Execute(TuiShortcutContext context) => context.ExitInteractiveMode();
}
