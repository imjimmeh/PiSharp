using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class AbortRequestShortcutCommand : TuiBuiltInShortcutCommandBase
{
    public override TuiKeybinding Binding { get; } = new("Esc", "Clear input / abort current request", "abort", TuiShortcutAction.AbortRequest, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.Esc], SlashCommand: "/abort", CommandTitle: "Abort current request");

    public override void Execute(TuiShortcutContext context) => context.AbortRequest();
}
