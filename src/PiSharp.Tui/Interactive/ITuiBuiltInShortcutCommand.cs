namespace PiSharp.Tui.Interactive;

public interface ITuiBuiltInShortcutCommand : ITuiShortcutCommand
{
    TuiKeybinding Binding { get; }
    TuiCommandDescriptor CommandDescriptor { get; }
    TuiHeaderHintDescriptor? HeaderHint { get; }
}
