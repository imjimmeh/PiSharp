using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ShowSessionTreeShortcutCommand : TuiBuiltInShortcutCommandBase
{
    public override TuiKeybinding Binding { get; } = new("Ctrl+R", "Show session tree", "tree", TuiShortcutAction.ShowSessionTree, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.R.WithCtrl], SlashCommand: "/session", CommandTitle: "Show session tree");

    public override void Execute(TuiShortcutContext context) => context.ShowSessionTree();
}
