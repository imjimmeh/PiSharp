using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed class ToggleLeftSidebarShortcutCommand : TuiBuiltInShortcutCommandBase
{
    public override TuiKeybinding Binding { get; } = new("F6", "Toggle left sidebar", "toggle-left-sidebar", TuiShortcutAction.ToggleLeftSidebar, TuiShortcutScope.GlobalApp, TuiShortcutRegistrationPolicy.GlobalShortcut, [Key.F6]);

    public override TuiHeaderHintDescriptor? HeaderHint => null;

    public override void Execute(TuiShortcutContext context) => context.ToggleLeftSidebar();
}
