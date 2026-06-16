namespace PiSharp.Tui.Interactive;

public sealed record TuiShortcutContext(
    Action AbortRequest,
    Action ExitInteractiveMode,
    Action ClearEditor,
    Action OpenModelSelector,
    Action ShowSessionTree,
    Action ToggleToolOutput,
    Action ToggleThinking,
    Action CycleThinkingLevel,
    Action ToggleHeaderDetails,
    Action CtrlC)
{
    public Action ToggleLeftSidebar { get; init; } = Noop;
    public Action ToggleRightSidebar { get; init; } = Noop;

    public TuiShortcutContext(
        Action AbortRequest,
        Action ExitInteractiveMode,
        Action ClearEditor,
        Action OpenModelSelector,
        Action ShowSessionTree,
        Action ToggleToolOutput,
        Action ToggleThinking,
        Action CycleThinkingLevel,
        Action ToggleHeaderDetails)
        : this(AbortRequest, ExitInteractiveMode, ClearEditor, OpenModelSelector, ShowSessionTree, ToggleToolOutput, ToggleThinking, CycleThinkingLevel, ToggleHeaderDetails, ClearEditor)
    {
    }

    public TuiShortcutContext()
        : this(Noop, Noop, Noop, Noop, Noop, Noop, Noop, Noop, Noop, Noop)
    {
    }

    private static void Noop()
    {
    }
}

public interface ITuiShortcutCommand
{
    TuiShortcutAction Action { get; }
    void Execute(TuiShortcutContext context);
}

public sealed class DelegateTuiShortcutCommand(TuiShortcutAction action, Action<TuiShortcutContext> execute) : ITuiShortcutCommand
{
    public TuiShortcutAction Action { get; } = action;

    public void Execute(TuiShortcutContext context) => execute(context);
}

public sealed class TuiShortcutDispatcher
{
    private readonly IReadOnlyDictionary<TuiShortcutAction, ITuiShortcutCommand> _commands;

    public TuiShortcutDispatcher(IEnumerable<ITuiShortcutCommand> commands)
    {
        _commands = commands.ToDictionary(command => command.Action);
    }

    public static TuiShortcutDispatcher CreateDefaultAppDispatcher()
        => new(TuiBuiltInShortcutCatalog.Commands);

    public bool TryDispatch(TuiShortcutAction action, TuiShortcutContext context)
    {
        if (!_commands.TryGetValue(action, out var command)) return false;

        command.Execute(context);
        return true;
    }
}
