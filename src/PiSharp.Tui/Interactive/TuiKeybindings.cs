using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public enum TuiShortcutAction
{
    SubmitPrompt,
    InsertNewline,
    AbortRequest,
    ExitInteractiveMode,
    OpenModelSelector,
    ShowSessionTree,
    ToggleToolOutput,
    ToggleThinking,
    CycleThinkingLevel,
    ToggleHeaderDetails,
    ClearEditor,
    NavigatePromptHistory,
    AcceptCompletion,
    ScrollTranscriptPage,
    ScrollTranscriptLine,
    ScrollTranscriptToStart,
    ScrollTranscriptToEnd,
    ToggleLeftSidebar,
    ToggleRightSidebar
}

public enum TuiShortcutScope
{
    GlobalApp,
    PromptEditor,
    TranscriptNavigation,
    HostInputPolicy
}

public enum TuiShortcutRegistrationPolicy
{
    GlobalShortcut,
    EditorLocal,
    HostInputPolicy
}

public sealed record TuiKeybinding(
    string Keys,
    string Description,
    string Action,
    TuiShortcutAction ShortcutAction,
    TuiShortcutScope Scope,
    TuiShortcutRegistrationPolicy RegistrationPolicy,
    IReadOnlyList<Key> TerminalKeys,
    string? SlashCommand = null,
    string? CommandTitle = null);

public static class TuiKeybindings
{
    public static readonly IReadOnlyList<TuiKeybinding> Defaults = TuiBuiltInShortcutCatalog.Bindings;

    public static IEnumerable<TuiKeybinding> GlobalShortcuts
        => TuiBuiltInShortcutCatalog.GlobalShortcuts;

    public static IReadOnlyList<TuiCommandDescriptor> CommandDescriptors { get; }
        = TuiBuiltInShortcutCatalog.CommandDescriptors;

    public static IReadOnlyList<TuiHeaderHintDescriptor> HeaderHints { get; }
        = TuiBuiltInShortcutCatalog.HeaderHints;

    public static string HotkeysText()
        => TuiHotkeyText.RenderFromDescriptors(CommandDescriptors, []);

    public static string HintText(string action, string fallback)
        => Defaults.FirstOrDefault(binding => string.Equals(binding.Action, action, StringComparison.Ordinal))?.Keys ?? fallback;
}
