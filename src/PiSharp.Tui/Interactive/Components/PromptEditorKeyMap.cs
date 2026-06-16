using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public sealed class PromptEditorKeyMap
{
    private readonly IReadOnlyList<PromptEditorKeyCommand> _commands;

    private PromptEditorKeyMap(IReadOnlyList<PromptEditorKeyCommand> commands)
    {
        _commands = commands;
    }

    public static PromptEditorKeyMap CreateEmpty()
        => new([]);

    public static PromptEditorKeyMap CreateDefault()
        => new(
        [
            new("submit", key => key.KeyCode == KeyCode.Enter && !key.IsShift && !key.IsCtrl, (controller, key) =>
            {
                _ = controller.SubmitAsync();
                return true;
            }),
            new("accept-suggestion", key => key.KeyCode == KeyCode.Tab, (controller, _) => Execute(controller.AcceptFirstSuggestion)),
            new("previous-choice", key => !key.IsCtrl && !key.IsAlt && key.KeyCode == KeyCode.CursorUp, (controller, _) => controller.TryHandleVerticalNavigation(-1)),
            new("next-choice", key => !key.IsCtrl && !key.IsAlt && key.KeyCode == KeyCode.CursorDown, (controller, _) => controller.TryHandleVerticalNavigation(1))
        ]);

    internal bool TryHandle(Key key, PromptEditorController controller)
    {
        foreach (var command in _commands)
        {
            if (!command.Matches(key)) continue;
            return command.Execute(controller, key);
        }

        return false;
    }

    private static bool Execute(Action action)
    {
        action();
        return true;
    }
}

internal sealed class PromptEditorKeyCommand(
    string name,
    Func<Key, bool> matches,
    Func<PromptEditorController, Key, bool> execute)
{
    public string Name { get; } = name;
    public bool Matches(Key key) => matches(key);
    public bool Execute(PromptEditorController controller, Key key) => execute(controller, key);
}
