using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ToggleRightSidebarShortcutCommand : TuiBuiltInShortcutCommandBase
{
    public override TuiKeybinding Binding { get; } = new("F7", "Toggle right sidebar", "toggle-right-sidebar", TuiShortcutAction.ToggleRightSidebar, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.F7]);

    public override TuiHeaderHintDescriptor? HeaderHint => null;

    public override void Execute(TuiShortcutContext context) => context.ToggleRightSidebar();
}
