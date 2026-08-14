using Terminal.Gui;
using PiSharp.Tui.Interactive.Keybindings;

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

    /// <summary>
    /// Process-wide effective binding table. <c>TuiHost</c> replaces this with the store it builds
    /// from <see cref="TuiHostOptions.KeybindingsPath"/> so every static consumer (header hints,
    /// /hotkeys, HintText, shortcut dispatch) honors user remaps.
    /// </summary>
    public static TuiKeybindingStore DefaultStore { get; set; } = new(TuiBuiltInShortcutCatalog.Bindings);

    public static IReadOnlyList<TuiCommandDescriptor> CommandDescriptors => DefaultStore.CommandDescriptors;

    public static IReadOnlyList<TuiHeaderHintDescriptor> HeaderHints => DefaultStore.HeaderHints;

    public static string HotkeysText() => DefaultStore.HotkeysText();

    public static string HintText(string action, string fallback) => DefaultStore.HintText(action, fallback);
}
