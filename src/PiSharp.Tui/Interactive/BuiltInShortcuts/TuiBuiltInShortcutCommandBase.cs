using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public abstract class TuiBuiltInShortcutCommandBase : ITuiBuiltInShortcutCommand
{
    public TuiShortcutAction Action => Binding.ShortcutAction;

    public abstract TuiKeybinding Binding { get; }

    public TuiCommandDescriptor CommandDescriptor => new(
        Binding.Action,
        Binding.CommandTitle ?? Binding.Description,
        Binding.SlashCommand,
        Binding.Keys,
        Binding.Description,
        Binding.ShortcutAction);

    public virtual TuiHeaderHintDescriptor? HeaderHint => null;

    public abstract void Execute(TuiShortcutContext context);
}
