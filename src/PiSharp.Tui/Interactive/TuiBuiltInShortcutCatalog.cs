using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public static class TuiBuiltInShortcutCatalog
{
    public static readonly IReadOnlyList<ITuiBuiltInShortcutCommand> Commands =
    [
        new AbortRequestShortcutCommand(),
        new ExitInteractiveModeShortcutCommand(),
        new OpenModelSelectorShortcutCommand(),
        new ShowSessionTreeShortcutCommand(),
        new ToggleToolOutputShortcutCommand(),
        new ToggleThinkingShortcutCommand(),
        new CycleThinkingLevelShortcutCommand(),
        new ToggleHeaderDetailsShortcutCommand(),
        new ClearEditorShortcutCommand(),
        new ToggleLeftSidebarShortcutCommand(),
        new ToggleRightSidebarShortcutCommand()
    ];

    public static readonly IReadOnlyList<TuiKeybinding> Bindings =
    [
        new TuiKeybinding("Enter", "Send prompt", "submit", TuiShortcutAction.SubmitPrompt, TuiShortcutScope.PromptEditor, TuiShortcutRegistrationPolicy.EditorLocal, [Key.Enter]),
        new TuiKeybinding("Shift+Enter/Ctrl+Enter", "Insert newline", "newline", TuiShortcutAction.InsertNewline, TuiShortcutScope.PromptEditor, TuiShortcutRegistrationPolicy.EditorLocal, [Key.Enter.WithShift, Key.Enter.WithCtrl]),
        .. Commands.Select(command => command.Binding),
        new TuiKeybinding("Up/Down", "Navigate prompt history when input is empty", "history", TuiShortcutAction.NavigatePromptHistory, TuiShortcutScope.PromptEditor, TuiShortcutRegistrationPolicy.EditorLocal, [Key.CursorUp, Key.CursorDown]),
        new TuiKeybinding("Tab", "Accept autocomplete", "complete", TuiShortcutAction.AcceptCompletion, TuiShortcutScope.PromptEditor, TuiShortcutRegistrationPolicy.EditorLocal, [Key.Tab]),
        new TuiKeybinding("PageUp/PageDown", "Scroll transcript by page", "scroll-page", TuiShortcutAction.ScrollTranscriptPage, TuiShortcutScope.TranscriptNavigation, TuiShortcutRegistrationPolicy.HostInputPolicy, [Key.PageUp, Key.PageDown]),
        new TuiKeybinding("Ctrl+Up/Down", "Scroll transcript by line", "scroll-line", TuiShortcutAction.ScrollTranscriptLine, TuiShortcutScope.TranscriptNavigation, TuiShortcutRegistrationPolicy.HostInputPolicy, [Key.CursorUp.WithCtrl, Key.CursorDown.WithCtrl]),
        new TuiKeybinding("Home", "Jump transcript to oldest message when input is empty", "scroll-start", TuiShortcutAction.ScrollTranscriptToStart, TuiShortcutScope.TranscriptNavigation, TuiShortcutRegistrationPolicy.HostInputPolicy, [Key.Home]),
        new TuiKeybinding("End", "Jump transcript to latest message", "scroll-end", TuiShortcutAction.ScrollTranscriptToEnd, TuiShortcutScope.TranscriptNavigation, TuiShortcutRegistrationPolicy.HostInputPolicy, [Key.End])
    ];

    public static readonly IReadOnlyList<TuiKeybinding> GlobalShortcuts =
        Bindings.Where(binding => binding.RegistrationPolicy == TuiShortcutRegistrationPolicy.GlobalShortcut).ToArray();

    public static readonly IReadOnlyList<TuiCommandDescriptor> CommandDescriptors =
        Bindings.Select(binding => new TuiCommandDescriptor(
            binding.Action,
            binding.CommandTitle ?? binding.Description,
            binding.SlashCommand,
            binding.Keys,
            binding.Description,
            binding.ShortcutAction)).ToArray();

    public static readonly IReadOnlyList<TuiHeaderHintDescriptor> HeaderHints =
        Commands.Select(command => command.HeaderHint).OfType<TuiHeaderHintDescriptor>().OrderBy(hint => hint.Order).ToArray();
}
